using System.Diagnostics;
using System.Text.RegularExpressions;
using EatHealthyCycle.Data;
using EatHealthyCycle.Models;
using EatHealthyCycle.Services.Interfaces;

namespace EatHealthyCycle.Services;

public class ImageImportService : IImageImportService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ImageImportService> _logger;

    private static readonly (string pattern, TipoComida tipo)[] MealPatterns =
    {
        ("pre\\s*desayuno", TipoComida.PreDesayuno),
        ("media\\s*ma[ñn]ana", TipoComida.MediaManana),
        ("tentempi[eé]", TipoComida.Almuerzo),
        ("desayuno", TipoComida.Desayuno),
        ("almuerzo", TipoComida.Almuerzo),
        ("comida", TipoComida.Comida),
        ("merienda", TipoComida.Merienda),
        ("cena", TipoComida.Cena)
    };

    private static readonly string[] DayNames =
    {
        "lunes", "martes", "miercoles", "jueves", "viernes", "sabado", "domingo"
    };

    private static readonly DayOfWeek[] DayOfWeekMap =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    };

    public ImageImportService(AppDbContext db, ILogger<ImageImportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Dieta> ImportarDietaDesdeImagenAsync(int usuarioId, string nombreDieta,
        Stream imageStream, string contentType, string nombreArchivo)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "eathealthy_ocr");
        Directory.CreateDirectory(tempDir);
        var tempImage = Path.Combine(tempDir, $"{Guid.NewGuid()}{GetExtension(contentType)}");

        try
        {
            await using (var fs = File.Create(tempImage))
            {
                await imageStream.CopyToAsync(fs);
            }

            _logger.LogInformation("Image saved to temp: {Path}, size: {Size} bytes, contentType: {CT}",
                tempImage, new FileInfo(tempImage).Length, contentType);

            var words = await RunTesseractAsync(tempImage);

            if (words.Count == 0)
                throw new InvalidOperationException(
                    "Tesseract OCR no pudo extraer texto de la imagen. " +
                    "Prueba con una imagen de mayor resolución o mejor calidad.");

            _logger.LogInformation("Tesseract extracted {Count} words from image", words.Count);

            var dieta = ParseTableFromWords(words, usuarioId, nombreDieta, nombreArchivo);

            _db.Dietas.Add(dieta);
            await _db.SaveChangesAsync();
            return dieta;
        }
        finally
        {
            if (File.Exists(tempImage)) File.Delete(tempImage);
        }
    }

    private async Task<List<OcrWord>> RunTesseractAsync(string imagePath)
    {
        _logger.LogInformation("Running Tesseract on image: {Path} (size: {Size} bytes)",
            imagePath, new FileInfo(imagePath).Length);

        // Try multiple PSM modes and languages until we get words
        var attempts = new[]
        {
            ("spa", "6"),   // PSM 6: assume single uniform block of text
            ("spa", "3"),   // PSM 3: fully automatic page segmentation
            ("spa", "4"),   // PSM 4: assume single column of variable-size text
            ("eng", "6"),   // Fallback to English
        };

        foreach (var (lang, psm) in attempts)
        {
            var words = await TryTesseractAsync(imagePath, lang, psm);
            if (words.Count > 0)
            {
                _logger.LogInformation("Success with lang={Lang} psm={Psm}: {Count} words", lang, psm, words.Count);
                return words;
            }
            _logger.LogInformation("No words with lang={Lang} psm={Psm}, trying next...", lang, psm);
        }

        return new List<OcrWord>();
    }

    private async Task<List<OcrWord>> TryTesseractAsync(string imagePath, string lang, string psm)
    {
        var tsvOutput = Path.Combine(Path.GetDirectoryName(imagePath)!, $"{Guid.NewGuid()}");

        try
        {
            // Force DPI to 300 for better recognition
            var psi = new ProcessStartInfo
            {
                FileName = "tesseract",
                Arguments = $"\"{imagePath}\" \"{tsvOutput}\" -l {lang} --psm {psm} --dpi 300 tsv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process? startedProcess = null;
            try
            {
                startedProcess = Process.Start(psi);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start tesseract process");
                throw new InvalidOperationException(
                    "Tesseract OCR no está instalado en el servidor. Contacta al administrador.");
            }

            if (startedProcess == null)
                throw new InvalidOperationException("No se pudo iniciar Tesseract OCR.");

            using var process = startedProcess;
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            _logger.LogInformation("Tesseract [lang={Lang} psm={Psm}] exit={Code}, stderr: {Err}",
                lang, psm, process.ExitCode, stderr.Length > 300 ? stderr[..300] : stderr);

            if (process.ExitCode != 0)
                return new List<OcrWord>();

            var tsvFile = tsvOutput + ".tsv";
            if (!File.Exists(tsvFile))
                return new List<OcrWord>();

            var lines = await File.ReadAllLinesAsync(tsvFile);
            _logger.LogInformation("TSV has {Lines} lines", lines.Length);

            // Log first 10 raw lines for debugging
            for (int i = 0; i < Math.Min(10, lines.Length); i++)
            {
                _logger.LogInformation("TSV[{I}]: {Line}", i, lines[i].Length > 200 ? lines[i][..200] : lines[i]);
            }

            // Also log some lines that have text (search for non-empty last column)
            var sampleWithText = lines.Skip(1)
                .Where(l => { var p = l.Split('\t'); return p.Length >= 12 && !string.IsNullOrWhiteSpace(p[11]); })
                .Take(5);
            foreach (var s in sampleWithText)
                _logger.LogInformation("TSV word sample: {Line}", s);

            return ParseTsvLines(lines);
        }
        finally
        {
            var tsvFile = tsvOutput + ".tsv";
            if (File.Exists(tsvFile)) File.Delete(tsvFile);
        }
    }

    internal List<OcrWord> ParseTsvLines(string[] lines)
    {
        var words = new List<OcrWord>();
        var skippedLowConf = 0;
        var skippedEmpty = 0;
        var skippedLevel = 0;

        // TSV header: level page_num block_num par_num line_num word_num left top width height conf text
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split('\t');

            // Need at least 11 columns (level through conf); text might be missing on structural lines
            if (parts.Length < 11) continue;

            // Only process word-level entries (level 5)
            if (!int.TryParse(parts[0], out var level)) continue;
            if (level != 5) { skippedLevel++; continue; }

            // Text is column 11 (index 11) - might be missing if line has exactly 11 columns
            var text = parts.Length > 11 ? parts[11].Trim() : "";
            // If text column missing, try joining remaining columns (text might contain tabs)
            if (string.IsNullOrWhiteSpace(text) && parts.Length > 12)
            {
                text = string.Join(" ", parts.Skip(11)).Trim();
            }
            if (string.IsNullOrWhiteSpace(text)) { skippedEmpty++; continue; }

            if (!int.TryParse(parts[6], out var left)) continue;
            if (!int.TryParse(parts[7], out var top)) continue;
            if (!int.TryParse(parts[8], out var width)) continue;
            if (!int.TryParse(parts[9], out var height)) continue;
            if (!int.TryParse(parts[10], out var conf)) continue;

            if (conf >= 0 && conf < 5) { skippedLowConf++; continue; }

            words.Add(new OcrWord
            {
                Text = text,
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                Right = left + width,
                Bottom = top + height,
                CenterX = left + width / 2.0,
                CenterY = top + height / 2.0,
                Confidence = conf,
                BlockNum = int.TryParse(parts[2], out var b) ? b : 0,
                LineNum = int.TryParse(parts[4], out var l) ? l : 0,
                ParNum = int.TryParse(parts[3], out var p) ? p : 0
            });
        }

        _logger.LogInformation("Parse results: {Words} words, {SkippedLevel} non-word lines, {SkippedEmpty} empty, {SkippedConf} low-conf",
            words.Count, skippedLevel, skippedEmpty, skippedLowConf);

        if (words.Count > 0)
        {
            var sampleWords = words.Take(30).Select(w => $"'{w.Text}'({w.Confidence})");
            _logger.LogInformation("Words sample: {Sample}", string.Join(", ", sampleWords));
        }

        return words;
    }

    private Dieta ParseTableFromWords(List<OcrWord> words, int usuarioId, string nombreDieta, string nombreArchivo)
    {
        var dieta = new Dieta
        {
            UsuarioId = usuarioId,
            Nombre = nombreDieta,
            ArchivoOriginal = nombreArchivo,
            Descripcion = "Importada desde imagen"
        };

        // Step 1: Find day columns
        var dayColumns = DetectDayColumns(words);
        _logger.LogInformation("Detected {Count} day columns", dayColumns.Count);

        if (dayColumns.Count == 0)
        {
            _logger.LogWarning("No day columns detected, attempting single-column parse");
            ParseSingleColumn(words, dieta);
            return dieta;
        }

        // Step 2: Find meal rows
        var mealRows = DetectMealRows(words, dayColumns);
        _logger.LogInformation("Detected {Count} meal rows", mealRows.Count);

        if (mealRows.Count == 0)
        {
            _logger.LogWarning("No meal rows detected, attempting single-column parse");
            ParseSingleColumn(words, dieta);
            return dieta;
        }

        // Step 3: Extract food items for each day/meal cell
        foreach (var day in dayColumns)
        {
            var dietaDia = new DietaDia
            {
                DiaSemana = day.DayOfWeek,
                Nota = day.Label
            };

            int orden = 0;
            foreach (var meal in mealRows)
            {
                var cellWords = GetCellWords(words, day, meal);
                var foodItems = ExtractFoodItems(cellWords);

                if (foodItems.Count > 0)
                {
                    var comida = new Comida
                    {
                        Tipo = meal.MealType,
                        Orden = orden++
                    };

                    foreach (var food in foodItems)
                    {
                        comida.Alimentos.Add(new Alimento
                        {
                            Nombre = food.Name,
                            Cantidad = food.Quantity
                        });
                    }

                    dietaDia.Comidas.Add(comida);
                }
            }

            if (dietaDia.Comidas.Count > 0)
                dieta.Dias.Add(dietaDia);
        }

        _logger.LogInformation("Image diet parsed: {Days} days, {Meals} total meals",
            dieta.Dias.Count, dieta.Dias.Sum(d => d.Comidas.Count));

        return dieta;
    }

    private List<DayColumn> DetectDayColumns(List<OcrWord> words)
    {
        var columns = new List<DayColumn>();

        // Look for day name headers (Lunes, Martes, etc.)
        foreach (var word in words)
        {
            var clean = NormalizeAccents(word.Text.Trim().ToLowerInvariant());

            for (int i = 0; i < DayNames.Length; i++)
            {
                if (clean == DayNames[i] || (clean.Length > 3 && DayNames[i].StartsWith(clean)))
                {
                    if (!columns.Any(c => c.DayOfWeek == DayOfWeekMap[i]))
                    {
                        columns.Add(new DayColumn
                        {
                            DayOfWeek = DayOfWeekMap[i],
                            Label = word.Text.Trim(),
                            HeaderCenterX = word.CenterX,
                            HeaderTop = word.Top,
                            HeaderLeft = word.Left,
                            HeaderRight = word.Right
                        });
                    }
                    break;
                }
            }
        }

        // Also look for "Día 1", "DIA 1" patterns
        var diaPattern = new Regex(@"^d[ií]a$", RegexOptions.IgnoreCase);
        var diaWords = words.Where(w => diaPattern.IsMatch(w.Text.Trim())).ToList();
        foreach (var diaWord in diaWords)
        {
            var numWord = words
                .Where(w => Math.Abs(w.CenterY - diaWord.CenterY) < diaWord.Height * 1.5
                    && w.Left > diaWord.Right - 5
                    && w.Left < diaWord.Right + diaWord.Width * 2
                    && int.TryParse(w.Text.Trim(), out _))
                .OrderBy(w => w.Left)
                .FirstOrDefault();

            if (numWord != null && int.TryParse(numWord.Text.Trim(), out var dayNum) && dayNum >= 1 && dayNum <= 7)
            {
                var dayOfWeek = (DayOfWeek)((dayNum % 7 == 0) ? 0 : dayNum);
                // Map: 1=Mon, 2=Tue, ... 7=Sun
                dayOfWeek = dayNum switch
                {
                    1 => DayOfWeek.Monday, 2 => DayOfWeek.Tuesday, 3 => DayOfWeek.Wednesday,
                    4 => DayOfWeek.Thursday, 5 => DayOfWeek.Friday, 6 => DayOfWeek.Saturday,
                    7 => DayOfWeek.Sunday, _ => DayOfWeek.Monday
                };

                if (!columns.Any(c => c.DayOfWeek == dayOfWeek))
                {
                    var combinedLeft = Math.Min(diaWord.Left, numWord.Left);
                    var combinedRight = Math.Max(diaWord.Right, numWord.Right);
                    columns.Add(new DayColumn
                    {
                        DayOfWeek = dayOfWeek,
                        Label = $"Día {dayNum}",
                        HeaderCenterX = (combinedLeft + combinedRight) / 2.0,
                        HeaderTop = Math.Min(diaWord.Top, numWord.Top),
                        HeaderLeft = combinedLeft,
                        HeaderRight = combinedRight
                    });
                }
            }
        }

        if (columns.Count == 0) return columns;

        columns = columns.OrderBy(c => c.HeaderCenterX).ToList();

        // Calculate column boundaries
        var imageWidth = words.Max(w => w.Right) + 10;
        for (int i = 0; i < columns.Count; i++)
        {
            columns[i].ContentLeft = i == 0
                ? 0
                : (columns[i - 1].HeaderCenterX + columns[i].HeaderCenterX) / 2;

            columns[i].ContentRight = i == columns.Count - 1
                ? imageWidth
                : (columns[i].HeaderCenterX + columns[i + 1].HeaderCenterX) / 2;
        }

        _logger.LogInformation("Day columns: {Cols}",
            string.Join(", ", columns.Select(c => $"{c.Label} [{c.ContentLeft:F0}-{c.ContentRight:F0}]")));

        return columns;
    }

    private List<MealRow> DetectMealRows(List<OcrWord> words, List<DayColumn> dayColumns)
    {
        var rows = new List<MealRow>();
        var headerTop = dayColumns.Min(c => c.HeaderTop);

        // Find meal labels - look for words matching meal patterns below the day headers
        // Meal labels are typically in the leftmost column or repeated for each day
        var candidateWords = words.Where(w => w.Top > headerTop).OrderBy(w => w.Top).ToList();

        foreach (var word in candidateWords)
        {
            var text = word.Text.Trim();

            // Try combining with next adjacent word(s) on same line for multi-word labels
            var nearby = candidateWords
                .Where(w => Math.Abs(w.CenterY - word.CenterY) < word.Height * 0.8
                    && w.Left > word.Left && w != word
                    && w.Left < word.Right + word.Width * 2)
                .OrderBy(w => w.Left)
                .Take(2)
                .ToList();

            var fullText = text;
            if (nearby.Count > 0)
                fullText = text + " " + string.Join(" ", nearby.Select(w => w.Text.Trim()));

            foreach (var (pattern, tipo) in MealPatterns)
            {
                if (Regex.IsMatch(fullText, pattern, RegexOptions.IgnoreCase))
                {
                    // Only add if not a duplicate at similar Y
                    if (!rows.Any(r => r.MealType == tipo && Math.Abs(r.Top - word.Top) < word.Height * 3))
                    {
                        rows.Add(new MealRow
                        {
                            MealType = tipo,
                            Label = fullText,
                            Top = word.Top,
                            CenterY = word.CenterY,
                            LabelLeft = word.Left,
                            LabelRight = nearby.Count > 0 ? nearby.Last().Right : word.Right
                        });
                    }
                    break;
                }
            }
        }

        rows = rows.OrderBy(r => r.Top).ToList();

        // Calculate row boundaries
        var imageHeight = words.Max(w => w.Bottom) + 10;
        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].ContentTop = i == 0
                ? rows[i].Top - 5
                : (rows[i - 1].CenterY + rows[i].CenterY) / 2;

            rows[i].ContentBottom = i == rows.Count - 1
                ? imageHeight
                : (rows[i].CenterY + rows[i + 1].CenterY) / 2;
        }

        _logger.LogInformation("Meal rows: {Rows}",
            string.Join(", ", rows.Select(r => $"{r.MealType} [{r.ContentTop:F0}-{r.ContentBottom:F0}]")));

        return rows;
    }

    private static List<OcrWord> GetCellWords(List<OcrWord> allWords, DayColumn day, MealRow meal)
    {
        return allWords
            .Where(w =>
                w.CenterX >= day.ContentLeft && w.CenterX <= day.ContentRight &&
                w.CenterY >= meal.ContentTop && w.CenterY <= meal.ContentBottom &&
                !IsMealLabel(w.Text) && !IsDayHeader(w.Text))
            .OrderBy(w => w.Top)
            .ThenBy(w => w.Left)
            .ToList();
    }

    private static List<FoodItem> ExtractFoodItems(List<OcrWord> cellWords)
    {
        if (cellWords.Count == 0) return new List<FoodItem>();

        // Group words into lines by Y position
        var lines = new List<List<OcrWord>>();
        List<OcrWord>? currentLine = null;

        foreach (var word in cellWords)
        {
            if (currentLine == null || !currentLine.Any(w => Math.Abs(w.CenterY - word.CenterY) < word.Height * 0.7))
            {
                currentLine = new List<OcrWord> { word };
                lines.Add(currentLine);
            }
            else
            {
                currentLine.Add(word);
            }
        }

        // Build text lines, joining continuation lines
        var textLines = new List<string>();
        foreach (var line in lines)
        {
            var lineText = string.Join(" ", line.OrderBy(w => w.Left).Select(w => w.Text.Trim()));
            lineText = CleanLine(lineText);
            if (string.IsNullOrWhiteSpace(lineText) || lineText.Length < 2) continue;

            if (textLines.Count > 0 && EndsWithPreposition(textLines[^1]))
            {
                textLines[^1] = textLines[^1] + " " + lineText;
            }
            else
            {
                textLines.Add(lineText);
            }
        }

        var items = new List<FoodItem>();
        foreach (var line in textLines)
        {
            var food = ParseFoodLine(line);
            if (food != null && food.Name.Length >= 2)
                items.Add(food);
        }

        return items;
    }

    private static FoodItem? ParseFoodLine(string line)
    {
        line = StripMealTypeFromLine(line).Trim();
        if (string.IsNullOrWhiteSpace(line) || line.Length < 2) return null;

        // Format 1: "Bebida de soja con calcio: 300g (1 taza)"
        var colonIdx = line.IndexOf(':');
        if (colonIdx > 2)
        {
            var name = line[..colonIdx].Trim().ToUpperInvariant();
            var qty = line[(colonIdx + 1)..].Trim();
            if (name.Length >= 2)
                return new FoodItem { Name = name, Quantity = string.IsNullOrWhiteSpace(qty) ? null : qty };
        }

        // Format 2: "HARINA DE AVENA 100G"
        var qtyMatch = Regex.Match(line, @"(\d+\s*[gGkK](?:g|r|R)?(?:\b|$)|\d+\s*[mM][lL]\b|\d+\s*[uU](?:nid|ds)?\.?\b|\d+\s*[cC][cC]\b|\d+\s*taza)", RegexOptions.IgnoreCase);
        if (qtyMatch.Success && qtyMatch.Index > 2)
        {
            var name = line[..qtyMatch.Index].Trim().ToUpperInvariant();
            var qty = line[qtyMatch.Index..].Trim();
            if (name.Length >= 2)
                return new FoodItem { Name = name, Quantity = qty };
        }

        // Format 3: just a name
        var cleanName = line.Trim().ToUpperInvariant();
        if (cleanName.Length >= 2)
            return new FoodItem { Name = cleanName };

        return null;
    }

    private void ParseSingleColumn(List<OcrWord> words, Dieta dieta)
    {
        var sortedWords = words.OrderBy(w => w.Top).ThenBy(w => w.Left).ToList();

        // Group into lines
        var lines = new List<string>();
        var currentLineWords = new List<OcrWord>();

        foreach (var word in sortedWords)
        {
            if (currentLineWords.Count > 0 &&
                Math.Abs(word.CenterY - currentLineWords[0].CenterY) > currentLineWords[0].Height * 0.7)
            {
                lines.Add(string.Join(" ", currentLineWords.OrderBy(w => w.Left).Select(w => w.Text)));
                currentLineWords.Clear();
            }
            currentLineWords.Add(word);
        }
        if (currentLineWords.Count > 0)
            lines.Add(string.Join(" ", currentLineWords.OrderBy(w => w.Left).Select(w => w.Text)));

        var dietaDia = new DietaDia { DiaSemana = DayOfWeek.Monday, Nota = "Día 1" };
        TipoComida currentMeal = TipoComida.Comida;
        var currentComida = new Comida { Tipo = currentMeal, Orden = 0 };
        int orden = 0;

        foreach (var rawLine in lines)
        {
            var line = CleanLine(rawLine);
            if (string.IsNullOrWhiteSpace(line) || line.Length < 2) continue;

            var dayMatch = DetectDayFromLine(line);
            if (dayMatch.HasValue)
            {
                if (currentComida.Alimentos.Count > 0)
                    dietaDia.Comidas.Add(currentComida);
                if (dietaDia.Comidas.Count > 0)
                    dieta.Dias.Add(dietaDia);

                dietaDia = new DietaDia { DiaSemana = dayMatch.Value, Nota = line };
                orden = 0;
                currentComida = new Comida { Tipo = currentMeal, Orden = orden };
                continue;
            }

            var mealType = DetectMealType(line);
            if (mealType.HasValue)
            {
                if (currentComida.Alimentos.Count > 0)
                    dietaDia.Comidas.Add(currentComida);
                currentMeal = mealType.Value;
                currentComida = new Comida { Tipo = currentMeal, Orden = orden++ };
                continue;
            }

            var food = ParseFoodLine(line);
            if (food != null && food.Name.Length >= 2)
            {
                currentComida.Alimentos.Add(new Alimento
                {
                    Nombre = food.Name,
                    Cantidad = food.Quantity
                });
            }
        }

        if (currentComida.Alimentos.Count > 0)
            dietaDia.Comidas.Add(currentComida);
        if (dietaDia.Comidas.Count > 0)
            dieta.Dias.Add(dietaDia);
    }

    private static bool IsMealLabel(string text)
    {
        var clean = text.Trim().ToLowerInvariant();
        return MealPatterns.Any(p => Regex.IsMatch(clean, p.pattern));
    }

    private static bool IsDayHeader(string text)
    {
        var clean = NormalizeAccents(text.Trim().ToLowerInvariant());
        return DayNames.Any(d => clean == d)
            || Regex.IsMatch(text.Trim(), @"^d[ií]a\s*\d*$", RegexOptions.IgnoreCase);
    }

    private static DayOfWeek? DetectDayFromLine(string line)
    {
        var clean = NormalizeAccents(line.Trim().ToLowerInvariant());

        for (int i = 0; i < DayNames.Length; i++)
        {
            if (clean.Contains(DayNames[i]))
                return DayOfWeekMap[i];
        }

        var diaMatch = Regex.Match(line, @"d[ií]a\s*(\d+)", RegexOptions.IgnoreCase);
        if (diaMatch.Success && int.TryParse(diaMatch.Groups[1].Value, out var num) && num >= 1 && num <= 7)
        {
            return num switch
            {
                1 => DayOfWeek.Monday, 2 => DayOfWeek.Tuesday, 3 => DayOfWeek.Wednesday,
                4 => DayOfWeek.Thursday, 5 => DayOfWeek.Friday, 6 => DayOfWeek.Saturday,
                7 => DayOfWeek.Sunday, _ => null
            };
        }

        return null;
    }

    private static TipoComida? DetectMealType(string line)
    {
        var clean = line.Trim().ToLowerInvariant();
        foreach (var (pattern, tipo) in MealPatterns)
        {
            if (Regex.IsMatch(clean, pattern))
                return tipo;
        }
        return null;
    }

    private static string StripMealTypeFromLine(string line)
    {
        var result = Regex.Replace(line,
            @"\b(pre\s*desayuno|desayuno|media\s*ma[ñn]ana|tentempi[eé]\s*\d*|almuerzo|comida|merienda|cena)\s*\d*\b",
            "", RegexOptions.IgnoreCase).Trim();
        result = Regex.Replace(result, @"^[\s\-:,]+", "").Trim();
        return result;
    }

    private static bool EndsWithPreposition(string text)
    {
        var words = text.TrimEnd().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        var last = words[^1].ToLowerInvariant();
        return last is "de" or "del" or "con" or "al" or "en" or "a" or "la" or "el" or "los" or "las" or "y" or "e" or "o" or "u";
    }

    private static string CleanLine(string line)
    {
        line = Regex.Replace(line, @"[\x00-\x1F\x7F]", " ");
        line = Regex.Replace(line, @"\s+", " ");
        line = Regex.Replace(line, @"[|\\{}\[\]<>]", " ");
        return line.Trim();
    }

    private static string NormalizeAccents(string text)
    {
        return text.Replace("á", "a").Replace("é", "e").Replace("í", "i")
            .Replace("ó", "o").Replace("ú", "u").Replace("ñ", "n");
    }

    private static string GetExtension(string contentType)
    {
        return contentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            _ => ".png"
        };
    }

    internal class OcrWord
    {
        public string Text { get; set; } = "";
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public int Confidence { get; set; }
        public int BlockNum { get; set; }
        public int LineNum { get; set; }
        public int ParNum { get; set; }
    }

    private class DayColumn
    {
        public DayOfWeek DayOfWeek { get; set; }
        public string Label { get; set; } = "";
        public double HeaderCenterX { get; set; }
        public int HeaderTop { get; set; }
        public int HeaderLeft { get; set; }
        public int HeaderRight { get; set; }
        public double ContentLeft { get; set; }
        public double ContentRight { get; set; }
    }

    private class MealRow
    {
        public TipoComida MealType { get; set; }
        public string Label { get; set; } = "";
        public int Top { get; set; }
        public double CenterY { get; set; }
        public int LabelLeft { get; set; }
        public int LabelRight { get; set; }
        public double ContentTop { get; set; }
        public double ContentBottom { get; set; }
    }

    private class FoodItem
    {
        public string Name { get; set; } = "";
        public string? Quantity { get; set; }
    }
}
