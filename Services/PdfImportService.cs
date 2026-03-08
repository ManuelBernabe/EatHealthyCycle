using System.Text;
using System.Text.RegularExpressions;
using EatHealthyCycle.Data;
using EatHealthyCycle.Models;
using EatHealthyCycle.Services.Interfaces;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace EatHealthyCycle.Services;

public class PdfImportService : IPdfImportService
{
    private readonly AppDbContext _db;
    private readonly ILogger<PdfImportService> _logger;

    private static readonly Dictionary<int, DayOfWeek> DiaNumeroMap = new()
    {
        [1] = DayOfWeek.Monday,
        [2] = DayOfWeek.Tuesday,
        [3] = DayOfWeek.Wednesday,
        [4] = DayOfWeek.Thursday,
        [5] = DayOfWeek.Friday,
        [6] = DayOfWeek.Saturday,
        [7] = DayOfWeek.Sunday
    };

    private static readonly (string pattern, TipoComida tipo)[] MealPatterns =
    {
        ("pre\\s*desayuno", TipoComida.PreDesayuno),
        ("media\\s*ma[ñn]ana", TipoComida.MediaManana),
        ("desayuno", TipoComida.Desayuno),
        ("almuerzo", TipoComida.Almuerzo),
        ("comida", TipoComida.Comida),
        ("merienda", TipoComida.Merienda),
        ("cena", TipoComida.Cena)
    };

    public PdfImportService(AppDbContext db, ILogger<PdfImportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Dieta> ImportarDietaDesdePdfAsync(int usuarioId, string nombreDieta, Stream pdfStream, string nombreArchivo)
    {
        var dieta = new Dieta
        {
            UsuarioId = usuarioId,
            Nombre = nombreDieta,
            ArchivoOriginal = nombreArchivo
        };

        using var document = PdfDocument.Open(pdfStream);
        var allPages = document.GetPages().ToList();

        _logger.LogInformation("PDF has {Pages} pages", allPages.Count);

        // Process table pages (columns with DIA headers)
        // Track last known column layout for continuation pages (pages without DIA headers)
        // Track orphaned words at the bottom of primary pages (belong to next page's first meal)
        var diasFromTables = new Dictionary<int, DietaDia>();
        List<(int diaNum, double contentLeft, double contentRight)>? lastColumns = null;
        Dictionary<int, List<Word>>? orphanedWords = null;
        foreach (var page in allPages)
        {
            (lastColumns, orphanedWords) = ProcessTablePage(page, diasFromTables, lastColumns, orphanedWords, _logger);
        }

        foreach (var kvp in diasFromTables.OrderBy(k => k.Key))
        {
            dieta.Dias.Add(kvp.Value);
        }

        // Process "Día off" from plain text sections
        var plainText = ExtractPlainText(allPages);
        ParseDiaOff(plainText, dieta);

        _logger.LogInformation("Diet parsed: {Days} days, {Meals} total meals",
            dieta.Dias.Count,
            dieta.Dias.Sum(d => d.Comidas.Count));

        foreach (var dia in dieta.Dias)
        {
            _logger.LogInformation("  Day {Day}: {Meals} meals",
                dia.DiaSemana, dia.Comidas.Count);
            foreach (var c in dia.Comidas)
            {
                _logger.LogInformation("    {Tipo}: {Foods}",
                    c.Tipo, string.Join(", ", c.Alimentos.Select(a => $"{a.Nombre} ({a.Cantidad})")));
            }
        }

        _db.Dietas.Add(dieta);
        await _db.SaveChangesAsync();
        return dieta;
    }

    // ==================== CHARACTER CLEANING ====================

    /// <summary>
    /// Whitelist-based cleaning: only keep letters (incl. accented), digits, and basic punctuation.
    /// This catches ALL garbage characters regardless of Unicode block.
    /// </summary>
    private static string CleanText(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '–' || c == '—' ||
                c == '(' || c == ')' || c == '.' || c == ',' || c == '+' || c == '/' ||
                c == 'á' || c == 'é' || c == 'í' || c == 'ó' || c == 'ú' || c == 'ñ' ||
                c == 'Á' || c == 'É' || c == 'Í' || c == 'Ó' || c == 'Ú' || c == 'Ñ' ||
                c == 'ü' || c == 'Ü')
                sb.Append(c);
        }
        return Regex.Replace(sb.ToString().Trim(), @"\s{2,}", " ");
    }

    /// <summary>
    /// Check if a word is a PURE bullet/symbol character (should be completely removed).
    /// Only matches words that are entirely non-alphanumeric (1-2 chars).
    /// </summary>
    private static bool IsBulletWord(Word word)
    {
        var text = word.Text.Trim();
        if (text.Length == 0) return false;
        if (text.Length > 2) return false;
        return text.All(c => !char.IsLetterOrDigit(c));
    }

    /// <summary>
    /// Check if a word starts with a bullet character (for line-start detection).
    /// Includes pure bullet words AND words with bullet prefix attached to text.
    /// </summary>
    private static bool StartsWithBullet(Word word)
    {
        var text = word.Text.Trim();
        if (text.Length == 0) return false;
        if (IsBulletWord(word)) return true;
        var first = text[0];
        return (first >= '\uF000' && first <= '\uF0FF') || // PUA (Wingdings)
               first == '•' || first == '●' || first == '○' ||
               first == '■' || first == '□' || first == '–' ||
               (!char.IsLetterOrDigit(first) && !char.IsWhiteSpace(first) && first != '(' && first != '-');
    }

    // ==================== TABLE PAGE PROCESSING ====================

    /// <summary>
    /// Process a table page. Returns column layout and any orphaned words at page bottom.
    /// </summary>
    private static (
        List<(int diaNum, double contentLeft, double contentRight)>? columns,
        Dictionary<int, List<Word>>? orphans
    ) ProcessTablePage(
        Page page,
        Dictionary<int, DietaDia> dias,
        List<(int diaNum, double contentLeft, double contentRight)>? previousColumns,
        Dictionary<int, List<Word>>? previousOrphans,
        ILogger logger)
    {
        var allWords = page.GetWords().ToList();
        if (allWords.Count == 0) return (previousColumns, null);

        // Step 1: Find DIA headers
        var diaHeaders = FindDiaHeaders(allWords);

        List<(int diaNum, double contentLeft, double contentRight)> dayColumns;

        if (diaHeaders.Count > 0)
        {
            // This is a primary page with DIA headers
            logger.LogInformation("Page {Page}: Found {Count} DIA headers: {Headers}",
                page.Number, diaHeaders.Count,
                string.Join(", ", diaHeaders.Select(h => $"DIA {h.diaNum} at X={h.xCenter:F0}")));

            // Find INGREDIENTES column headers
            var ingredientesHeaders = allWords
                .Where(w => CleanText(w.Text).Equals("INGREDIENTES", StringComparison.OrdinalIgnoreCase))
                .Select(w => new { xLeft = w.BoundingBox.Left, xRight = w.BoundingBox.Right, xCenter = (w.BoundingBox.Left + w.BoundingBox.Right) / 2 })
                .OrderBy(w => w.xLeft)
                .ToList();

            // Define column boundaries for each day
            dayColumns = new List<(int diaNum, double contentLeft, double contentRight)>();

            for (int i = 0; i < diaHeaders.Count; i++)
            {
                var dia = diaHeaders[i];
                var closestIngr = ingredientesHeaders
                    .Where(ig => Math.Abs(ig.xCenter - dia.xCenter) < 200)
                    .OrderBy(ig => Math.Abs(ig.xCenter - dia.xCenter))
                    .FirstOrDefault();

                double contentLeft, contentRight;

                if (closestIngr != null)
                {
                    contentLeft = closestIngr.xLeft - 20;
                    contentRight = i + 1 < diaHeaders.Count
                        ? diaHeaders[i + 1].xLeft - 30
                        : page.Width;
                }
                else
                {
                    var pageWidth = page.Width;
                    var colWidth = pageWidth / diaHeaders.Count;
                    contentLeft = dia.xCenter - colWidth / 4;
                    contentRight = dia.xCenter + colWidth / 2;
                }

                dayColumns.Add((dia.diaNum, contentLeft, contentRight));
            }
        }
        else if (previousColumns != null)
        {
            // Continuation page: no DIA headers, reuse previous column layout
            logger.LogInformation("Page {Page}: No DIA headers, using previous column layout ({Count} columns)",
                page.Number, previousColumns.Count);
            dayColumns = previousColumns;
        }
        else
        {
            return (null, null); // No headers and no previous layout
        }

        // Step 4: Find all unique meal type labels with their Y positions
        var mealLabels = FindMealLabels(allWords);

        logger.LogInformation("Page {Page}: Found {Count} meal labels: {Labels}",
            page.Number, mealLabels.Count,
            string.Join(", ", mealLabels.Select(m => $"{m.tipo} at Y={m.yCenter:F0}")));

        if (mealLabels.Count == 0) return (dayColumns, null);

        // Step 5: Define meal row boundaries
        var sortedMeals = mealLabels.OrderByDescending(m => m.yCenter).ToList();
        var mealRows = new List<(TipoComida tipo, double yTop, double yBottom)>();

        // Calculate minimum spacing for bottom boundary heuristic
        double minMealSpacing = 150;
        if (sortedMeals.Count >= 2)
        {
            var spacings = new List<double>();
            for (int s = 0; s < sortedMeals.Count - 1; s++)
                spacings.Add(sortedMeals[s].yCenter - sortedMeals[s + 1].yCenter);
            minMealSpacing = spacings.Min();
        }

        for (int i = 0; i < sortedMeals.Count; i++)
        {
            var yTop = i == 0
                ? sortedMeals[i].yCenter + 50
                : (sortedMeals[i].yCenter + sortedMeals[i - 1].yCenter) / 2;
            var yBottom = i + 1 < sortedMeals.Count
                ? (sortedMeals[i].yCenter + sortedMeals[i + 1].yCenter) / 2
                : Math.Max(0, sortedMeals[i].yCenter - minMealSpacing * 1.5);

            mealRows.Add((sortedMeals[i].tipo, yTop, yBottom));
        }

        // On continuation pages, merge orphaned words from previous page into the first meal
        if (previousOrphans != null && previousOrphans.Count > 0)
        {
            var firstMealRow = mealRows.First(); // Topmost meal on this page
            logger.LogInformation("Page {Page}: Merging {Count} orphan groups into {Meal}",
                page.Number, previousOrphans.Count, firstMealRow.tipo);

            foreach (var col in dayColumns)
            {
                if (previousOrphans.TryGetValue(col.diaNum, out var orphanWords) && orphanWords.Count > 0)
                {
                    // Get words in this column's first meal cell
                    var cellWords = allWords
                        .Where(w =>
                        {
                            var wx = (w.BoundingBox.Left + w.BoundingBox.Right) / 2;
                            var wy = (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2;
                            return wx >= col.contentLeft && wx <= col.contentRight &&
                                   wy >= firstMealRow.yBottom && wy <= firstMealRow.yTop;
                        })
                        .ToList();

                    // Combine orphan words with cell words
                    var combined = new List<Word>(orphanWords);
                    combined.AddRange(cellWords);

                    if (!dias.ContainsKey(col.diaNum))
                    {
                        dias[col.diaNum] = new DietaDia
                        {
                            DiaSemana = DiaNumeroMap.GetValueOrDefault(col.diaNum, (DayOfWeek)(col.diaNum % 7)),
                            Nota = $"Día {col.diaNum}"
                        };
                    }

                    var dia = dias[col.diaNum];
                    var existingComida = dia.Comidas.FirstOrDefault(c => c.Tipo == firstMealRow.tipo);
                    if (existingComida == null)
                    {
                        existingComida = new Comida { Tipo = firstMealRow.tipo, Orden = dia.Comidas.Count };
                        dia.Comidas.Add(existingComida);
                    }

                    var foodItems = ExtractFoodItemsFromCell(combined);
                    foreach (var foodText in foodItems)
                    {
                        var (nombre, cantidad) = ParseAlimento(foodText);
                        nombre = CleanText(nombre).Trim();
                        if (string.IsNullOrWhiteSpace(nombre) || nombre.Length < 2) continue;
                        if (!existingComida.Alimentos.Any(a =>
                            CleanText(a.Nombre).Equals(nombre, StringComparison.OrdinalIgnoreCase)))
                        {
                            existingComida.Alimentos.Add(new Alimento { Nombre = nombre, Cantidad = cantidad });
                        }
                    }
                }
            }
        }

        // Step 6: For each day column x each meal row, extract food items
        foreach (var col in dayColumns)
        {
            if (!dias.ContainsKey(col.diaNum))
            {
                dias[col.diaNum] = new DietaDia
                {
                    DiaSemana = DiaNumeroMap.GetValueOrDefault(col.diaNum, (DayOfWeek)(col.diaNum % 7)),
                    Nota = $"Día {col.diaNum}"
                };
            }

            var dia = dias[col.diaNum];
            int orden = dia.Comidas.Count;

            foreach (var row in mealRows)
            {
                // Get ALL words in this cell (including bullet words for delimiter detection)
                var cellWords = allWords
                    .Where(w =>
                    {
                        var wx = (w.BoundingBox.Left + w.BoundingBox.Right) / 2;
                        var wy = (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2;
                        return wx >= col.contentLeft && wx <= col.contentRight &&
                               wy >= row.yBottom && wy <= row.yTop;
                    })
                    .ToList();

                if (cellWords.Count == 0) continue;

                // Extract food items using bullet-aware merging
                var foodItems = ExtractFoodItemsFromCell(cellWords);

                if (foodItems.Count == 0) continue;

                var existingComida = dia.Comidas.FirstOrDefault(c => c.Tipo == row.tipo);
                if (existingComida == null)
                {
                    existingComida = new Comida { Tipo = row.tipo, Orden = orden++ };
                    dia.Comidas.Add(existingComida);
                }

                foreach (var foodText in foodItems)
                {
                    var (nombre, cantidad) = ParseAlimento(foodText);
                    nombre = CleanText(nombre).Trim();
                    if (string.IsNullOrWhiteSpace(nombre) || nombre.Length < 2) continue;
                    // Skip noise that got through earlier filters
                    if (IsNoiseLine(nombre)) continue;

                    if (!existingComida.Alimentos.Any(a =>
                        CleanText(a.Nombre).Equals(nombre, StringComparison.OrdinalIgnoreCase)))
                    {
                        existingComida.Alimentos.Add(new Alimento { Nombre = nombre, Cantidad = cantidad });
                    }
                }
            }
        }

        // Detect orphaned content at the bottom of the page.
        // Look for a significant Y gap (>40pt) in the last meal's cell content.
        // Items below the gap belong to the first meal on the continuation page.
        Dictionary<int, List<Word>>? newOrphans = null;
        var lastMealRow = mealRows.Last();

        foreach (var col in dayColumns)
        {
            var cellWords = allWords
                .Where(w =>
                {
                    var wx = (w.BoundingBox.Left + w.BoundingBox.Right) / 2;
                    var wy = (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2;
                    return wx >= col.contentLeft && wx <= col.contentRight &&
                           wy >= lastMealRow.yBottom && wy <= lastMealRow.yTop;
                })
                .Where(w => !IsBulletWord(w))
                .OrderByDescending(w => w.BoundingBox.Bottom)
                .ToList();

            if (cellWords.Count < 2) continue;

            // Find the largest Y gap between consecutive words
            double maxGap = 0;
            double gapBoundary = 0;
            for (int g = 0; g < cellWords.Count - 1; g++)
            {
                var gap = cellWords[g].BoundingBox.Bottom - cellWords[g + 1].BoundingBox.Bottom;
                if (gap > maxGap)
                {
                    maxGap = gap;
                    gapBoundary = (cellWords[g].BoundingBox.Bottom + cellWords[g + 1].BoundingBox.Bottom) / 2;
                }
            }

            if (maxGap > 40) // Significant gap found - items below belong to next page's first meal
            {
                var orphans = allWords
                    .Where(w =>
                    {
                        var wx = (w.BoundingBox.Left + w.BoundingBox.Right) / 2;
                        var wy = (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2;
                        return wx >= col.contentLeft && wx <= col.contentRight &&
                               wy < gapBoundary && wy > 0;
                    })
                    .ToList();

                if (orphans.Count > 0)
                {
                    newOrphans ??= new Dictionary<int, List<Word>>();
                    newOrphans[col.diaNum] = orphans;
                    logger.LogInformation("Page {Page}: {Count} orphan words for DIA {Dia} (gap={Gap:F0}pt at Y={Boundary:F0})",
                        page.Number, orphans.Count, col.diaNum, maxGap, gapBoundary);

                    // Remove orphaned items from the last meal's comida
                    if (dias.TryGetValue(col.diaNum, out var dia))
                    {
                        var lastComida = dia.Comidas.LastOrDefault();
                        if (lastComida != null)
                        {
                            var orphanFoods = ExtractFoodItemsFromCell(orphans);
                            foreach (var of in orphanFoods)
                            {
                                var cleanName = CleanText(ParseAlimento(of).nombre).Trim();
                                var toRemove = lastComida.Alimentos
                                    .FirstOrDefault(a => CleanText(a.Nombre).Equals(cleanName, StringComparison.OrdinalIgnoreCase));
                                if (toRemove != null) lastComida.Alimentos.Remove(toRemove);
                            }
                        }
                    }
                }
            }
        }

        return (dayColumns, newOrphans);
    }

    /// <summary>
    /// Extract food items from a table cell using bullet-aware line merging.
    /// Key insight: bullet characters mark the START of a new food item.
    /// Lines without a leading bullet are continuations of the previous item.
    /// </summary>
    private static List<string> ExtractFoodItemsFromCell(List<Word> cellWords)
    {
        if (cellWords.Count == 0) return new List<string>();

        // Group words into lines by Y position (tolerance 4pt)
        var sorted = cellWords.OrderByDescending(w => w.BoundingBox.Bottom).ToList();
        var lineGroups = new List<List<Word>>();
        var currentLine = new List<Word> { sorted[0] };
        var currentY = sorted[0].BoundingBox.Bottom;

        for (int i = 1; i < sorted.Count; i++)
        {
            if (Math.Abs(sorted[i].BoundingBox.Bottom - currentY) <= 4)
            {
                currentLine.Add(sorted[i]);
            }
            else
            {
                lineGroups.Add(currentLine);
                currentLine = new List<Word> { sorted[i] };
                currentY = sorted[i].BoundingBox.Bottom;
            }
        }
        lineGroups.Add(currentLine);

        // Build clean lines with bullet info
        var cleanLines = new List<(string text, bool hasBullet)>();
        bool anyBullets = false;

        foreach (var lineWords in lineGroups)
        {
            var orderedWords = lineWords.OrderBy(w => w.BoundingBox.Left).ToList();
            bool startsWithBullet = StartsWithBullet(orderedWords[0]);
            if (startsWithBullet) anyBullets = true;

            // Only remove pure bullet words; CleanText will strip bullet chars from attached words
            var cleanedParts = orderedWords
                .Where(w => !IsBulletWord(w))
                .Select(w => CleanText(w.Text))
                .Where(t => !string.IsNullOrWhiteSpace(t));
            var lineText = string.Join(" ", cleanedParts).Trim();

            if (string.IsNullOrWhiteSpace(lineText) || lineText.Length < 2) continue;
            if (IsNoiseLine(lineText) || IsMealTypeHeader(lineText)) continue;

            cleanLines.Add((lineText, startsWithBullet));
        }

        var foodItems = new List<string>();

        if (anyBullets)
        {
            // Bullet-aware merging: bullet = new item, no bullet = continuation
            var currentItemParts = new List<string>();
            foreach (var (lineText, hasBullet) in cleanLines)
            {
                if (hasBullet && currentItemParts.Count > 0)
                {
                    var itemText = string.Join(" ", currentItemParts);
                    if (!IsNoiseLine(itemText) && !IsMealTypeHeader(itemText))
                        foodItems.Add(StripMealTypeFromFood(itemText));
                    currentItemParts.Clear();
                }
                currentItemParts.Add(lineText);
            }
            if (currentItemParts.Count > 0)
            {
                var itemText = string.Join(" ", currentItemParts);
                if (!IsNoiseLine(itemText) && !IsMealTypeHeader(itemText))
                    foodItems.Add(StripMealTypeFromFood(itemText));
            }
        }
        else
        {
            // No bullets detected: use quantity-boundary heuristic.
            // A new food item starts when a line begins with a WORD (not a quantity continuation).
            // Lines that are just quantities or adjectives append to previous item.
            // CRITICAL: If the previous accumulated text ends with a preposition (DE, DEL, CON, etc.),
            // the next line is ALWAYS a continuation, even if it looks like a new food item.
            var currentItemParts = new List<string>();
            foreach (var (lineText, _) in cleanLines)
            {
                bool prevEndsWithPreposition = currentItemParts.Count > 0 &&
                    EndsWithPreposition(currentItemParts[^1]);

                if (currentItemParts.Count > 0 && !prevEndsWithPreposition && LooksLikeNewFoodItem(lineText))
                {
                    var itemText = string.Join(" ", currentItemParts);
                    if (!IsNoiseLine(itemText) && !IsMealTypeHeader(itemText))
                        foodItems.Add(StripMealTypeFromFood(itemText));
                    currentItemParts.Clear();
                }
                currentItemParts.Add(lineText);
            }
            if (currentItemParts.Count > 0)
            {
                var itemText = string.Join(" ", currentItemParts);
                if (!IsNoiseLine(itemText) && !IsMealTypeHeader(itemText))
                    foodItems.Add(StripMealTypeFromFood(itemText));
            }
        }

        return foodItems;
    }

    // ==================== HEADER DETECTION ====================

    private static List<(int diaNum, double xLeft, double xCenter)> FindDiaHeaders(List<Word> words)
    {
        var result = new List<(int diaNum, double xLeft, double xCenter)>();

        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var cleanW = CleanText(w.Text);
            if (!Regex.IsMatch(cleanW, @"^DIA$|^D[ÍI]A$", RegexOptions.IgnoreCase))
                continue;

            for (int j = i + 1; j < Math.Min(i + 5, words.Count); j++)
            {
                var next = words[j];
                if (Math.Abs(next.BoundingBox.Bottom - w.BoundingBox.Bottom) > 15)
                    continue;

                var cleanNext = CleanText(next.Text);
                if (int.TryParse(cleanNext, out var num) && num >= 1 && num <= 7)
                {
                    var xCenter = (w.BoundingBox.Left + next.BoundingBox.Right) / 2;
                    if (!result.Any(r => r.diaNum == num))
                        result.Add((num, w.BoundingBox.Left, xCenter));
                    break;
                }
            }
        }

        return result.OrderBy(r => r.xLeft).ToList();
    }

    private static List<(TipoComida tipo, double yCenter, double xCenter)> FindMealLabels(List<Word> words)
    {
        var result = new List<(TipoComida tipo, double yCenter, double xCenter)>();
        var processed = new HashSet<int>();

        // Find "PRE DESAYUNO" (two words, possibly on different lines within tolerance)
        for (int i = 0; i < words.Count; i++)
        {
            if (processed.Contains(i)) continue;
            var w = words[i];
            var cleanW = CleanText(w.Text);

            if (Regex.IsMatch(cleanW, @"^PRE$", RegexOptions.IgnoreCase))
            {
                for (int j = i + 1; j < Math.Min(i + 6, words.Count); j++)
                {
                    if (Math.Abs(words[j].BoundingBox.Bottom - w.BoundingBox.Bottom) > 25)
                        continue;
                    var cleanJ = CleanText(words[j].Text);
                    if (Regex.IsMatch(cleanJ, @"^DESAYUNO$", RegexOptions.IgnoreCase))
                    {
                        var yc = (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2;
                        var xc = (w.BoundingBox.Left + words[j].BoundingBox.Right) / 2;
                        result.Add((TipoComida.PreDesayuno, yc, xc));
                        processed.Add(i);
                        processed.Add(j);
                        break;
                    }
                }
            }
        }

        // Find single-word meal types
        for (int i = 0; i < words.Count; i++)
        {
            if (processed.Contains(i)) continue;
            var w = words[i];
            var cleanW = CleanText(w.Text);

            foreach (var (pattern, tipo) in MealPatterns)
            {
                if (pattern.Contains("\\s")) continue;
                if (Regex.IsMatch(cleanW, $@"^{pattern}$", RegexOptions.IgnoreCase))
                {
                    var yc = (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2;
                    var xc = (w.BoundingBox.Left + w.BoundingBox.Right) / 2;
                    result.Add((tipo, yc, xc));
                    processed.Add(i);
                    break;
                }
            }
        }

        // Deduplicate: keep one label per meal type per distinct Y band
        var deduped = result
            .GroupBy(r => r.tipo)
            .SelectMany(g =>
            {
                var sortedG = g.OrderByDescending(r => r.yCenter).ToList();
                var unique = new List<(TipoComida tipo, double yCenter, double xCenter)>();
                foreach (var item in sortedG)
                {
                    if (!unique.Any(u => Math.Abs(u.yCenter - item.yCenter) < 30))
                        unique.Add(item);
                }
                return unique;
            })
            .ToList();

        return deduped;
    }

    // ==================== FOOD ITEM PARSING ====================

    private static (string nombre, string? cantidad) ParseAlimento(string linea)
    {
        linea = CleanText(linea).Trim();

        // "HARINA DE AVENA- 100G" or "HARINA DE AVENA 100G"
        var matchSuffix = Regex.Match(linea,
            @"^(.+?)\s*[\-–]?\s*(\d+\s*(?:g|gr|mg|kg|ml|l|cl|ud|unidades?|rebanadas?|cucharadas?|vasos?|tazas?|piezas?|latas?))\s*$",
            RegexOptions.IgnoreCase);
        if (matchSuffix.Success)
            return (matchSuffix.Groups[1].Value.Trim(), matchSuffix.Groups[2].Value.Trim());

        // "PROTEINA WHEY 20G" (quantity embedded)
        var matchEmbedded = Regex.Match(linea,
            @"^(.+?)\s+(\d+\s*(?:G|GR|MG|KG|ML|L|CL))\b(.*)$",
            RegexOptions.IgnoreCase);
        if (matchEmbedded.Success)
        {
            var nombre = matchEmbedded.Groups[1].Value.Trim();
            var cantidad = matchEmbedded.Groups[2].Value.Trim();
            var extra = matchEmbedded.Groups[3].Value.Trim();
            if (!string.IsNullOrWhiteSpace(extra) && !Regex.IsMatch(extra, @"^\s*[\(\)]"))
                nombre += " " + extra;
            return (nombre, cantidad);
        }

        // "3 REBANADAS (60G)" or "2 UNIDADES"
        var matchPrefix = Regex.Match(linea,
            @"^(\d+\s*(?:unidades?|rebanadas?|cucharadas?|latas?|rodajas?))\s+(?:de\s+)?(.+)$",
            RegexOptions.IgnoreCase);
        if (matchPrefix.Success)
            return (matchPrefix.Groups[2].Value.Trim(), matchPrefix.Groups[1].Value.Trim());

        // "PAN (60G)" or "AGUACATE (100G)"
        var matchParen = Regex.Match(linea, @"^(.+?)\s*\((.+?)\)\s*$");
        if (matchParen.Success)
            return (matchParen.Groups[1].Value.Trim(), matchParen.Groups[2].Value.Trim());

        return (linea.Trim(), null);
    }

    // ==================== DÍA OFF (PLAIN TEXT) ====================

    private static void ParseDiaOff(string texto, Dieta dieta)
    {
        var match = Regex.Match(texto, @"D[ií]a\s+off[^:]*:", RegexOptions.IgnoreCase);
        if (!match.Success) return;
        if (dieta.Dias.Any(d => d.DiaSemana == DayOfWeek.Sunday)) return;

        var diaOff = new DietaDia
        {
            DiaSemana = DayOfWeek.Sunday,
            Nota = "Día de descanso"
        };

        var section = texto[match.Index..];
        var lines = section.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        Comida? comidaActual = null;
        int orden = 0;

        foreach (var rawLine in lines.Skip(1))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (Regex.IsMatch(line, @"suplementaci[oó]n", RegexOptions.IgnoreCase)) break;

            var mealMatch = Regex.Match(line, @"^-?\s*(desayuno|almuerzo|comida|merienda|cena)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
            if (mealMatch.Success)
            {
                var tipoStr = mealMatch.Groups[1].Value.ToLowerInvariant();
                var tipo = tipoStr switch
                {
                    "desayuno" => TipoComida.Desayuno,
                    "almuerzo" => TipoComida.Almuerzo,
                    "comida" => TipoComida.Comida,
                    "merienda" => TipoComida.Merienda,
                    "cena" => TipoComida.Cena,
                    _ => TipoComida.Comida
                };

                comidaActual = new Comida { Tipo = tipo, Orden = orden++ };
                diaOff.Comidas.Add(comidaActual);

                var resto = mealMatch.Groups[2].Value.Trim();
                if (!string.IsNullOrWhiteSpace(resto))
                    ParseDiaOffFoodLine(comidaActual, resto);
                continue;
            }

            if (comidaActual != null && !string.IsNullOrWhiteSpace(line))
                ParseDiaOffFoodLine(comidaActual, line);
        }

        // "Cena: IGUAL A LOS DÍAS DE ACTIVIDAD FÍSICA"
        var cena = diaOff.Comidas.FirstOrDefault(c => c.Tipo == TipoComida.Cena);
        if (cena != null && cena.Alimentos.Count == 0)
        {
            var otraCena = dieta.Dias
                .SelectMany(d => d.Comidas)
                .FirstOrDefault(c => c.Tipo == TipoComida.Cena && c.Alimentos.Count > 0);
            if (otraCena != null)
            {
                foreach (var al in otraCena.Alimentos)
                    cena.Alimentos.Add(new Alimento { Nombre = al.Nombre, Cantidad = al.Cantidad });
            }
        }

        if (diaOff.Comidas.Count > 0)
            dieta.Dias.Add(diaOff);
    }

    private static void ParseDiaOffFoodLine(Comida comida, string line)
    {
        var items = Regex.Split(line, @"\s*\+\s*");
        foreach (var item in items)
        {
            var cleaned = CleanText(item).Trim('-', ' ');
            if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length < 2) continue;

            if (Regex.IsMatch(cleaned, @"\s+o\s+", RegexOptions.IgnoreCase))
            {
                var alternatives = Regex.Split(cleaned, @"\s+o\s+", RegexOptions.IgnoreCase);
                foreach (var alt in alternatives)
                    AddFoodItem(comida, alt.Trim());
                continue;
            }

            if (Regex.IsMatch(cleaned, @"igual\s+a\s+los", RegexOptions.IgnoreCase))
                continue;

            AddFoodItem(comida, cleaned);
        }
    }

    private static void AddFoodItem(Comida comida, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return;
        var (nombre, cantidad) = ParseAlimento(text);
        nombre = CleanText(nombre).Trim();
        if (string.IsNullOrWhiteSpace(nombre) || nombre.Length < 2) return;
        if (!comida.Alimentos.Any(a => CleanText(a.Nombre).Equals(nombre, StringComparison.OrdinalIgnoreCase)))
            comida.Alimentos.Add(new Alimento { Nombre = nombre, Cantidad = cantidad });
    }

    // ==================== PLAIN TEXT EXTRACTION ====================

    private static string ExtractPlainText(List<Page> pages)
    {
        var allLines = new List<string>();
        foreach (var page in pages)
        {
            var words = page.GetWords().ToList();
            if (words.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(page.Text))
                    allLines.Add(CleanText(page.Text));
                continue;
            }

            var lines = words
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 0))
                .OrderByDescending(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left)
                    .Where(w => !IsBulletWord(w))
                    .Select(w => CleanText(w.Text))
                    .Where(t => !string.IsNullOrWhiteSpace(t))).Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length >= 2);

            allLines.AddRange(lines);
        }
        return string.Join("\n", allLines);
    }

    /// <summary>
    /// Heuristic: does this line look like a NEW food item (vs continuation of previous)?
    /// Used when no bullets are detected to split cell text into items.
    /// </summary>
    private static bool LooksLikeNewFoodItem(string lineText)
    {
        var t = lineText.Trim();
        if (t.Length < 2) return false;

        // If it starts with a digit, it's likely a quantity continuation (e.g., "100G", "2 REBANADAS")
        if (char.IsDigit(t[0])) return false;

        // If it starts with a lowercase letter, likely continuation
        if (char.IsLower(t[0])) return false;

        // Known adjectives/modifiers are continuations, not new items
        var firstWord = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).First();
        var adjectives = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HERVIDO", "COCIDO", "FRESCO", "INTEGRAL", "NATURAL", "PASTEURIZADA",
            "PELADA", "PELADO", "DESGRASADO", "TOSTADO", "RALLADO", "GRANDE", "TIPO"
        };
        if (adjectives.Contains(firstWord)) return false;

        // Otherwise, an uppercase-starting word is likely a new food item
        return true;
    }

    // ==================== HELPERS ====================

    private static readonly HashSet<string> Prepositions = new(StringComparer.OrdinalIgnoreCase)
    {
        "DE", "DEL", "CON", "AL", "EN", "A", "LA", "LAS", "LOS"
    };

    /// <summary>
    /// Check if a text ends with a preposition (DE, DEL, CON, etc.)
    /// meaning the next line is a continuation, not a new food item.
    /// </summary>
    private static bool EndsWithPreposition(string text)
    {
        var words = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        return Prepositions.Contains(words[^1]);
    }

    private static readonly HashSet<string> MealTypeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "DESAYUNO", "ALMUERZO", "COMIDA", "MERIENDA", "CENA"
    };

    /// <summary>
    /// Remove meal type words (COMIDA, CENA, etc.) that leaked into food names.
    /// E.g., "SAL YODADA COMIDA" → "SAL YODADA"
    /// </summary>
    private static string StripMealTypeFromFood(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cleaned = words.Where(w => !MealTypeWords.Contains(w)).ToArray();
        return string.Join(" ", cleaned);
    }

    // ==================== NOISE FILTERS ====================

    private static bool IsNoiseLine(string linea)
    {
        var l = linea.Trim().ToLowerInvariant();
        if (l.Length < 2) return true;
        if (l == "pre") return true;
        if (Regex.IsMatch(l, @"^(ingesta|ingredientes|rganutri|plan\s+nutricional|cuestionario|gasto\s+energ|necesidades\s+h[ií]dricas|datos\s+antropom|check\s+inicial|fecha|cita|peso|talla|perfil|espalda|manu)\b"))
            return true;
        if (Regex.IsMatch(l, @"^[\d\.\…]+$")) return true;
        if (Regex.IsMatch(l, @"^entrenamiento\s+de\s+", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(l, @"^dia\s+de\s+descanso$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(l, @"^d[ií]a\s+\d+$", RegexOptions.IgnoreCase)) return true;

        // Medication/supplement schedule noise
        if (l.Contains("masteron") || l.Contains("telmisartan") || l.Contains("ursobilane") ||
            l.Contains("cipionato") || l.Contains("capsula") || l.Contains("1ml") ||
            l.Contains("enantate") || l.Contains("vitamina c") || l.Contains("omega 3"))
            return true;
        // Día off / supplement section text
        if (l.Contains("día off") || l.Contains("dia off") || l.Contains("no entreno") ||
            l.Contains("igual a los") || l.Contains("actividad fís") || l.Contains("actividad fis") ||
            l.Contains("actividada") || // typo variant in some PDFs
            l.Contains("suplementación") || l.Contains("suplementacion") ||
            l.Contains("de dormir"))
            return true;
        // Pure quantity/dosage items with no food name: "(300mg)", "(1000mg)", "(1G)"
        if (Regex.IsMatch(l, @"^\(?\d+\s*(mg|g|ml|l|mcg)\)?$")) return true;
        // Standalone prepositions or articles that got orphaned
        if (l.Length <= 3 && Prepositions.Contains(l.Trim())) return true;
        // Lines starting with "-" followed by meal names (Día off format: "-Desayuno ...", "-Comida ...")
        if (Regex.IsMatch(l, @"^-?\s*nac\b")) return true;
        if (Regex.IsMatch(l, @"^-\s*(desayuno|almuerzo|comida|merienda|cena)\b")) return true;
        // Long lines with mixed meal labels inside (typical of Día off / supplement blobs)
        if (l.Length > 60) return true;
        // Contains "+" as separator (supplement combinations: "proteína whey 40g+ canela")
        if (l.Contains("+") && !Regex.IsMatch(l, @"^\d")) return true;

        return false;
    }

    private static bool IsMealTypeHeader(string linea)
    {
        var l = CleanText(linea).Trim().ToLowerInvariant();
        l = Regex.Replace(l, @"^[\-\s]+", "");
        // Standalone "PRE" is part of PRE DESAYUNO
        if (l == "pre") return true;
        foreach (var (pattern, _) in MealPatterns)
        {
            if (Regex.IsMatch(l, $@"^{pattern}$", RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }
}
