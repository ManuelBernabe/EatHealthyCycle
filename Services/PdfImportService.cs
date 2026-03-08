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
        var diasFromTables = new Dictionary<int, DietaDia>();
        foreach (var page in allPages)
        {
            ProcessTablePage(page, diasFromTables, _logger);
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
            _logger.LogInformation("  Day {Day}: {Meals} meals, {Foods} total foods",
                dia.DiaSemana, dia.Comidas.Count,
                dia.Comidas.Sum(c => c.Alimentos.Count));
        }

        _db.Dietas.Add(dieta);
        await _db.SaveChangesAsync();
        return dieta;
    }

    /// <summary>
    /// Clean a word's text: strip bullet/box chars, trim whitespace.
    /// </summary>
    private static string CleanWordText(string text)
    {
        // Remove □, •, ■, ▪, ●, ○ and other common bullet/box characters
        var cleaned = Regex.Replace(text, @"[\u25A0-\u25FF\u2022\u2023\u25E6\u2043\u2219\u25CB\u25CF\u25A1\u25AA\u25AB□■•●○]", "").Trim();
        return cleaned;
    }

    /// <summary>
    /// Process a single PDF page that contains a table with day columns.
    /// Each day has a pair of columns: INGESTA (meal type) + INGREDIENTES (food items).
    /// </summary>
    private static void ProcessTablePage(Page page, Dictionary<int, DietaDia> dias, ILogger logger)
    {
        var rawWords = page.GetWords().ToList();
        if (rawWords.Count == 0) return;

        // Clean all words: strip bullet/box characters, filter out empty/garbage
        var words = rawWords
            .Select(w => (word: w, cleanText: CleanWordText(w.Text)))
            .Where(x => !string.IsNullOrWhiteSpace(x.cleanText) && x.cleanText.Length >= 1)
            .Select(x => x.word)
            .ToList();

        // Step 1: Find DIA headers and their X positions
        var diaHeaders = FindDiaHeaders(words);
        if (diaHeaders.Count == 0) return;

        logger.LogInformation("Page {Page}: Found {Count} DIA headers: {Headers}",
            page.Number, diaHeaders.Count,
            string.Join(", ", diaHeaders.Select(h => $"DIA {h.diaNum} at X={h.xCenter:F0}")));

        // Step 2: Find INGREDIENTES column headers
        var ingredientesHeaders = words
            .Where(w => w.Text.Equals("INGREDIENTES", StringComparison.OrdinalIgnoreCase))
            .Select(w => new { xLeft = w.BoundingBox.Left, xRight = w.BoundingBox.Right, xCenter = (w.BoundingBox.Left + w.BoundingBox.Right) / 2 })
            .OrderBy(w => w.xLeft)
            .ToList();

        // Step 3: Define column boundaries for each day
        var dayColumns = new List<(int diaNum, double contentLeft, double contentRight)>();

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

        // Step 4: Find all unique meal type labels with their Y positions
        var mealLabels = FindMealLabels(words);

        logger.LogInformation("Page {Page}: Found {Count} meal labels: {Labels}",
            page.Number, mealLabels.Count,
            string.Join(", ", mealLabels.Select(m => $"{m.tipo} at Y={m.yCenter:F0}")));

        if (mealLabels.Count == 0) return;

        // Step 5: Define meal row boundaries
        var sortedMeals = mealLabels.OrderByDescending(m => m.yCenter).ToList();
        var mealRows = new List<(TipoComida tipo, double yTop, double yBottom)>();

        for (int i = 0; i < sortedMeals.Count; i++)
        {
            var yTop = i == 0
                ? sortedMeals[i].yCenter + 50
                : (sortedMeals[i].yCenter + sortedMeals[i - 1].yCenter) / 2;
            var yBottom = i + 1 < sortedMeals.Count
                ? (sortedMeals[i].yCenter + sortedMeals[i + 1].yCenter) / 2
                : 0;

            mealRows.Add((sortedMeals[i].tipo, yTop, yBottom));
        }

        // Step 6: For each day column x each meal row, collect food words
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
                var cellWords = words
                    .Where(w =>
                    {
                        var wx = (w.BoundingBox.Left + w.BoundingBox.Right) / 2;
                        var wy = (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2;
                        return wx >= col.contentLeft && wx <= col.contentRight &&
                               wy >= row.yBottom && wy <= row.yTop;
                    })
                    .ToList();

                if (cellWords.Count == 0) continue;

                var lines = GroupWordsIntoLines(cellWords);
                var foodLines = lines
                    .Where(l => !IsNoiseLine(l) && !IsMealTypeHeader(l))
                    .ToList();

                if (foodLines.Count == 0) continue;

                var existingComida = dia.Comidas.FirstOrDefault(c => c.Tipo == row.tipo);
                if (existingComida == null)
                {
                    existingComida = new Comida { Tipo = row.tipo, Orden = orden++ };
                    dia.Comidas.Add(existingComida);
                }

                var fullText = string.Join("\n", foodLines);
                ParseFoodItems(existingComida, fullText);
            }
        }
    }

    private static List<(int diaNum, double xLeft, double xCenter)> FindDiaHeaders(List<Word> words)
    {
        var result = new List<(int diaNum, double xLeft, double xCenter)>();

        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (!Regex.IsMatch(w.Text, @"^DIA$|^D[ÍI]A$", RegexOptions.IgnoreCase))
                continue;

            for (int j = i + 1; j < Math.Min(i + 5, words.Count); j++)
            {
                var next = words[j];
                if (Math.Abs(next.BoundingBox.Bottom - w.BoundingBox.Bottom) > 15)
                    continue;

                if (int.TryParse(next.Text, out var num) && num >= 1 && num <= 7)
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

        // Find "PRE DESAYUNO" (two words)
        for (int i = 0; i < words.Count; i++)
        {
            if (processed.Contains(i)) continue;
            var w = words[i];

            if (Regex.IsMatch(w.Text, @"^PRE$", RegexOptions.IgnoreCase))
            {
                for (int j = i + 1; j < Math.Min(i + 4, words.Count); j++)
                {
                    if (Math.Abs(words[j].BoundingBox.Bottom - w.BoundingBox.Bottom) > 20)
                        continue;
                    if (Regex.IsMatch(words[j].Text, @"^DESAYUNO$", RegexOptions.IgnoreCase))
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
            var text = w.Text.Trim();

            foreach (var (pattern, tipo) in MealPatterns)
            {
                if (pattern.Contains("\\s")) continue;
                if (Regex.IsMatch(text, $@"^{pattern}$", RegexOptions.IgnoreCase))
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
                var sorted = g.OrderByDescending(r => r.yCenter).ToList();
                var unique = new List<(TipoComida tipo, double yCenter, double xCenter)>();
                foreach (var item in sorted)
                {
                    if (!unique.Any(u => Math.Abs(u.yCenter - item.yCenter) < 30))
                        unique.Add(item);
                }
                return unique;
            })
            .ToList();

        return deduped;
    }

    private static List<string> GroupWordsIntoLines(List<Word> words)
    {
        if (words.Count == 0) return new List<string>();

        // Group words by Y position with tolerance of 4 points
        var sorted = words.OrderByDescending(w => w.BoundingBox.Bottom).ToList();
        var lines = new List<List<Word>>();
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
                lines.Add(currentLine);
                currentLine = new List<Word> { sorted[i] };
                currentY = sorted[i].BoundingBox.Bottom;
            }
        }
        lines.Add(currentLine);

        return lines
            .Select(lineWords =>
            {
                var text = string.Join(" ", lineWords.OrderBy(w => w.BoundingBox.Left)
                    .Select(w => CleanWordText(w.Text))
                    .Where(t => !string.IsNullOrWhiteSpace(t)));
                // Collapse multiple spaces
                return Regex.Replace(text, @"\s{2,}", " ").Trim();
            })
            .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length >= 2)
            .ToList();
    }

    private static void ParseFoodItems(Comida comida, string texto)
    {
        // Clean all □/bullet chars from the entire text first
        texto = Regex.Replace(texto, @"[\u25A0-\u25FF\u2022\u2023\u25E6\u2043\u2219\u25CB\u25CF\u25A1\u25AA\u25AB□■•●○]", " ");
        texto = Regex.Replace(texto, @"\s{2,}", " ");

        var lines = texto.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = Regex.Replace(rawLine, @"^[\-•\*\s]+", "").Trim();
            if (string.IsNullOrWhiteSpace(line) || line.Length < 2) continue;
            if (IsNoiseLine(line)) continue;
            if (IsMealTypeHeader(line)) continue;

            // Split on common separators: bullets, commas between food items, "+"
            var items = Regex.Split(line, @"[•\u2022]\s*");
            foreach (var item in items)
            {
                var cleaned = item.Trim().Trim('-', '*', ' ');
                if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length < 2) continue;
                if (IsMealTypeHeader(cleaned)) continue;

                var (nombre, cantidad) = ParseAlimento(cleaned);
                nombre = NormalizeFoodName(nombre);
                if (string.IsNullOrWhiteSpace(nombre) || nombre.Length < 2) continue;

                if (!comida.Alimentos.Any(a => NormalizeFoodName(a.Nombre).Equals(nombre, StringComparison.OrdinalIgnoreCase)))
                {
                    comida.Alimentos.Add(new Alimento { Nombre = nombre, Cantidad = cantidad });
                }
            }
        }
    }

    /// <summary>
    /// Normalize a food name: strip special chars, collapse whitespace, title case.
    /// </summary>
    private static string NormalizeFoodName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        // Strip box/bullet characters
        name = Regex.Replace(name, @"[\u25A0-\u25FF\u2022\u2023\u25E6\u2043\u2219\u25CB\u25CF\u25A1\u25AA\u25AB□■•●○]", "");
        // Collapse whitespace
        name = Regex.Replace(name, @"\s{2,}", " ").Trim();
        // Remove trailing dashes/hyphens
        name = name.Trim('-', '–', '—', ' ');
        return name;
    }

    private static (string nombre, string? cantidad) ParseAlimento(string linea)
    {
        linea = linea.Trim().Trim('-', '•', '*', ' ');

        // "HARINA DE AVENA- 100G"
        var matchSuffix = Regex.Match(linea,
            @"^(.+?)\s*[\-–]?\s*(\d+\s*(?:g|gr|mg|kg|ml|l|cl|ud|unidades?|rebanadas?|cucharadas?|vasos?|tazas?|piezas?|latas?))\s*$",
            RegexOptions.IgnoreCase);
        if (matchSuffix.Success)
            return (matchSuffix.Groups[1].Value.Trim(), matchSuffix.Groups[2].Value.Trim());

        // "PROTEINA WHEY 20G"
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

        // "2 UNIDADES de X"
        var matchPrefix = Regex.Match(linea,
            @"^(\d+\s*(?:unidades?|rebanadas?|cucharadas?|latas?|rodajas?))\s+(?:de\s+)?(.+)$",
            RegexOptions.IgnoreCase);
        if (matchPrefix.Success)
            return (matchPrefix.Groups[2].Value.Trim(), matchPrefix.Groups[1].Value.Trim());

        // "PAN (60G)"
        var matchParen = Regex.Match(linea, @"^(.+?)\s*\((.+?)\)\s*$");
        if (matchParen.Success)
            return (matchParen.Groups[1].Value.Trim(), matchParen.Groups[2].Value.Trim());

        return (linea.Trim(), null);
    }

    /// <summary>
    /// Parse the "Día off" section (plain text, not table)
    /// </summary>
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
            var cleaned = item.Trim().Trim('-', '•', '*');
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
        if (string.IsNullOrWhiteSpace(nombre) || nombre.Length < 2) return;
        if (!comida.Alimentos.Any(a => a.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase)))
            comida.Alimentos.Add(new Alimento { Nombre = nombre, Cantidad = cantidad });
    }

    private static string ExtractPlainText(List<Page> pages)
    {
        var allLines = new List<string>();
        foreach (var page in pages)
        {
            var words = page.GetWords().ToList();
            if (words.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(page.Text))
                    allLines.Add(Regex.Replace(page.Text, @"[\u25A0-\u25FF\u2022\u2023\u25E6\u2043\u2219\u25CB\u25CF\u25A1\u25AA\u25AB□■•●○]", " "));
                continue;
            }

            var lines = words
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 0))
                .OrderByDescending(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left)
                    .Select(w => CleanWordText(w.Text))
                    .Where(t => !string.IsNullOrWhiteSpace(t))).Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length >= 2);

            allLines.AddRange(lines);
        }
        return string.Join("\n", allLines);
    }

    private static bool IsNoiseLine(string linea)
    {
        var l = linea.Trim().ToLowerInvariant();
        if (l.Length < 2) return true;
        if (Regex.IsMatch(l, @"^(ingesta|ingredientes|rganutri|plan\s+nutricional|cuestionario|gasto\s+energ|necesidades\s+h[ií]dricas|datos\s+antropom|check\s+inicial|fecha|cita|peso|talla|perfil|espalda|manu)\b"))
            return true;
        if (Regex.IsMatch(l, @"^[\d\.\…]+$")) return true;
        if (Regex.IsMatch(l, @"^entrenamiento\s+de\s+", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(l, @"^dia\s+de\s+descanso$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(l, @"^d[ií]a\s+\d+$", RegexOptions.IgnoreCase)) return true;
        return false;
    }

    private static bool IsMealTypeHeader(string linea)
    {
        var l = linea.Trim().ToLowerInvariant();
        l = Regex.Replace(l, @"^[\-•\*\s]+", "");
        foreach (var (pattern, _) in MealPatterns)
        {
            if (Regex.IsMatch(l, $@"^{pattern}$", RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }
}
