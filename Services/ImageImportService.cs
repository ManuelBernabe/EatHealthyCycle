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

            // Detect colored meal header bars before OCR
            var colorBands = await DetectColorBandsAsync(tempImage);

            var words = await RunTesseractAsync(tempImage);

            if (words.Count == 0)
                throw new InvalidOperationException(
                    "Tesseract OCR no pudo extraer texto de la imagen. " +
                    "Prueba con una imagen de mayor resolución o mejor calidad.");

            _logger.LogInformation("Tesseract extracted {Count} words from image", words.Count);

            var dieta = ParseTableFromWords(words, usuarioId, nombreDieta, nombreArchivo, colorBands);

            _db.Dietas.Add(dieta);
            await _db.SaveChangesAsync();
            return dieta;
        }
        finally
        {
            if (File.Exists(tempImage)) File.Delete(tempImage);
        }
    }

    public async Task<object> DiagnosticAnalyzeAsync(Stream imageStream, string contentType)
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

            var colorBands = await DetectColorBandsAsync(tempImage);

            var words = await RunTesseractAsync(tempImage);
            if (words.Count == 0)
                return new { error = "No words extracted", wordCount = 0 };

            var dayColumns = DetectDayColumns(words);
            var mealRows = dayColumns.Count > 0 ? DetectMealRows(words, dayColumns, colorBands) : new List<MealRow>();

            var cells = new List<object>();
            foreach (var day in dayColumns)
            {
                foreach (var meal in mealRows)
                {
                    var cellWords = GetCellWords(words, day, meal);
                    var foodItems = ExtractFoodItems(cellWords);

                    cells.Add(new
                    {
                        day = day.Label,
                        dayCol = $"{day.ContentLeft:F0}-{day.ContentRight:F0}",
                        meal = meal.MealType.ToString(),
                        mealY = $"{meal.ContentTop:F0}-{meal.ContentBottom:F0}",
                        wordCount = cellWords.Count,
                        words = cellWords.Take(20).Select(w => new
                        {
                            text = w.Text, x = w.Left, y = w.Top, w = w.Width, h = w.Height,
                            block = w.BlockNum, line = w.LineNum, par = w.ParNum, conf = w.Confidence
                        }),
                        foodItems = foodItems.Select(f => new { name = f.Name, qty = f.Quantity }),
                        rawLines = cellWords.Count > 0 ? GetDiagnosticLines(cellWords) : Array.Empty<string>()
                    });
                }
            }

            return new
            {
                totalWords = words.Count,
                colorBands = colorBands.Select(b => new { top = b.top, bottom = b.bottom }),
                columns = dayColumns.Select(c => new { label = c.Label, centerX = c.HeaderCenterX, left = c.ContentLeft, right = c.ContentRight }),
                meals = mealRows.Select(m => new { type = m.MealType.ToString(), top = m.ContentTop, bottom = m.ContentBottom }),
                sampleWords = words.Take(50).Select(w => new { text = w.Text, x = w.Left, y = w.Top, w = w.Width, h = w.Height, block = w.BlockNum, line = w.LineNum }),
                cells
            };
        }
        finally
        {
            if (File.Exists(tempImage)) File.Delete(tempImage);
        }
    }

    private static string[] GetDiagnosticLines(List<OcrWord> cellWords)
    {
        var sorted = cellWords.OrderBy(w => w.CenterY).ThenBy(w => w.Left).ToList();
        var avgHeight = sorted.Average(w => (double)w.Height);
        var lineBreakThreshold = Math.Max(avgHeight * 0.4, 8);

        var lines = new List<string>();
        var currentLine = new List<OcrWord> { sorted[0] };

        for (int i = 1; i < sorted.Count; i++)
        {
            var gap = sorted[i].CenterY - sorted[i - 1].CenterY;
            if (gap > lineBreakThreshold)
            {
                lines.Add($"[Y={currentLine[0].Top},gap={gap:F0}] " +
                    string.Join(" ", currentLine.OrderBy(w => w.Left).Select(w => w.Text)));
                currentLine = new List<OcrWord> { sorted[i] };
            }
            else
            {
                currentLine.Add(sorted[i]);
            }
        }
        lines.Add($"[Y={currentLine[0].Top}] " +
            string.Join(" ", currentLine.OrderBy(w => w.Left).Select(w => w.Text)));

        return lines.ToArray();
    }

    // Preprocessing strategies: try gentlest first, then more aggressive
    private static readonly string[] PreprocessStrategies =
    {
        "",  // No preprocessing - original image (Tesseract handles color natively)
        "-colorspace Gray",  // Just grayscale, preserve anti-aliasing
        "-colorspace Gray -level 25%,75% -sharpen 0x1",  // High contrast
        "-colorspace Gray -level 30%,60% -threshold 50%",  // Binary B&W
    };

    private async Task<List<OcrWord>> RunTesseractAsync(string imagePath)
    {
        _logger.LogInformation("Running Tesseract on image: {Path} (size: {Size} bytes)",
            imagePath, new FileInfo(imagePath).Length);

        List<OcrWord>? bestResult = null;
        string bestDesc = "";

        // Upscale image 2x before OCR — Tesseract accuracy improves significantly
        // on larger images (better character resolution, clearer line separation).
        var (ocrBase, scaleFactor) = await UpscaleImageForOcrAsync(imagePath);
        try
        {
            // Try each strategy with PSM 6 (uniform block) first - best for table images.
            // PSM 4 (single column) and PSM 11 (sparse) as fallbacks.
            // Do NOT use PSM 3 (fully automatic) - it reorganizes table content.
            foreach (var strategy in PreprocessStrategies)
            {
                string inputPath;
                if (string.IsNullOrEmpty(strategy))
                {
                    inputPath = ocrBase;
                }
                else
                {
                    inputPath = await PreprocessImageAsync(ocrBase, strategy);
                }

                try
                {
                    foreach (var (lang, psm) in new[] { ("spa", "6"), ("spa", "4"), ("spa", "11") })
                    {
                        var words = await TryTesseractAsync(inputPath, lang, psm);
                        var desc = $"strategy='{strategy}' lang={lang} psm={psm}";

                        if (words.Count > 0)
                        {
                            _logger.LogInformation("{Desc}: {Count} words, avgConf={Avg:F0}",
                                desc, words.Count, words.Average(w => w.Confidence));

                            if (bestResult == null || words.Count > bestResult.Count)
                            {
                                bestResult = words;
                                bestDesc = desc;
                            }
                        }
                    }
                }
                finally
                {
                    if (inputPath != ocrBase && File.Exists(inputPath))
                        File.Delete(inputPath);
                }

                // Use first strategy that gives a decent result (>50 words).
                // Original image or gentle grayscale preserves spatial structure best.
                if (bestResult != null && bestResult.Count > 50)
                {
                    _logger.LogInformation("Using result: {Desc} with {Count} words", bestDesc, bestResult.Count);
                    break;
                }
            }
        }
        finally
        {
            if (ocrBase != imagePath && File.Exists(ocrBase))
                File.Delete(ocrBase);
        }

        _logger.LogInformation("Final best result: {Desc} with {Count} words",
            bestDesc, bestResult?.Count ?? 0);

        var result = bestResult ?? new List<OcrWord>();

        // Scale word coordinates back to original image space so they align with
        // color band boundaries (which are detected on the original image).
        if (scaleFactor > 1.0 && result.Count > 0)
        {
            foreach (var word in result)
            {
                word.Left   = (int)(word.Left   / scaleFactor);
                word.Top    = (int)(word.Top    / scaleFactor);
                word.Width  = Math.Max(1, (int)(word.Width  / scaleFactor));
                word.Height = Math.Max(1, (int)(word.Height / scaleFactor));
                word.Right  = word.Left + word.Width;
                word.Bottom = word.Top  + word.Height;
                word.CenterX = word.Left + word.Width  / 2.0;
                word.CenterY = word.Top  + word.Height / 2.0;
            }
            _logger.LogInformation("Scaled {Count} word coordinates by 1/{Factor:F1} to original image space",
                result.Count, scaleFactor);
        }

        return result;
    }

    /// <summary>
    /// Upscale image 2x using ImageMagick for better OCR quality.
    /// Returns (upscaledPath, scaleFactor). Falls back to (original, 1.0) if IM unavailable.
    /// </summary>
    private async Task<(string path, double scaleFactor)> UpscaleImageForOcrAsync(string imagePath)
    {
        const double scale = 2.0;
        var suffix = $"_2x{Guid.NewGuid():N}.png";
        var upscaled = Path.Combine(Path.GetDirectoryName(imagePath)!,
            $"{Path.GetFileNameWithoutExtension(imagePath)}{suffix}");

        var psi = new ProcessStartInfo
        {
            FileName = "convert",
            Arguments = $"\"{imagePath}\" -resize 200% -filter Lanczos \"{upscaled}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                _logger.LogWarning("ImageMagick not available for upscaling, using original image");
                return (imagePath, 1.0);
            }

            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("ImageMagick upscaling failed: {Err}", stderr);
                return (imagePath, 1.0);
            }

            var size = new FileInfo(upscaled).Length;
            _logger.LogInformation("Image upscaled 2x for OCR: {Path} ({Size} bytes)", upscaled, size);
            return (upscaled, scale);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Upscaling failed, using original image");
            if (File.Exists(upscaled)) File.Delete(upscaled);
            return (imagePath, 1.0);
        }
    }

    private async Task<string> PreprocessImageAsync(string imagePath, string convertArgs)
    {
        var suffix = $"_pre{Guid.NewGuid():N}.png";
        var preprocessed = Path.Combine(Path.GetDirectoryName(imagePath)!,
            $"{Path.GetFileNameWithoutExtension(imagePath)}{suffix}");

        var psi = new ProcessStartInfo
        {
            FileName = "convert",
            Arguments = $"\"{imagePath}\" {convertArgs} \"{preprocessed}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                _logger.LogWarning("ImageMagick not available, using original image");
                return imagePath;
            }

            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("ImageMagick preprocessing failed: {Err}", stderr);
                return imagePath;
            }

            _logger.LogInformation("Image preprocessed ({Args}): {Path}", convertArgs, preprocessed);
            return preprocessed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ImageMagick not installed, using original image");
            return imagePath;
        }
    }

    /// <summary>
    /// Use ImageMagick to detect colored horizontal bars (meal headers) in the image.
    /// Tries multiple crop strategies: full width average, left strip, center strip.
    /// Returns list of (top, bottom) Y ranges for each colored band found.
    /// </summary>
    internal async Task<List<(int top, int bottom)>> DetectColorBandsAsync(string imagePath)
    {
        // Try multiple crop strategies - meal header bars may not span full width
        var strategies = new[]
        {
            "-resize 1x! -depth 8",                           // Full width average
            "-crop 60x0+0+0 +repage -resize 1x! -depth 8",   // Left 60px strip
            "-crop 60x0+500+0 +repage -resize 1x! -depth 8",  // Center strip
        };

        List<(int top, int bottom)> bestBands = new();

        foreach (var strategy in strategies)
        {
            var bands = await DetectColorBandsWithStrategy(imagePath, strategy);
            _logger.LogInformation("Color detection strategy '{S}': {Count} bands", strategy, bands.Count);
            if (bands.Count > bestBands.Count)
                bestBands = bands;
            if (bestBands.Count >= 4)
                break; // Found enough bands
        }

        if (bestBands.Count > 0)
            _logger.LogInformation("Color band detection: {Count} bands at Y=[{Bands}]",
                bestBands.Count, string.Join(", ", bestBands.Select(b => $"{b.top}-{b.bottom}")));

        return bestBands;
    }

    private async Task<List<(int top, int bottom)>> DetectColorBandsWithStrategy(string imagePath, string convertArgs)
    {
        var bands = new List<(int top, int bottom)>();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "convert",
                Arguments = $"\"{imagePath}\" {convertArgs} txt:-",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return bands;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) return bands;

            // Parse pixel data - handle both srgb() and srgba() formats (IM6 vs IM7)
            // Also handle raw (R,G,B) format as fallback
            var coloredYs = new List<int>();
            foreach (var line in output.Split('\n'))
            {
                if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line)) continue;

                // Try srgb/srgba format first, then raw (R,G,B) format
                var match = Regex.Match(line, @"0,(\d+):.*srgba?\((\d+),(\d+),(\d+)");
                if (!match.Success)
                    match = Regex.Match(line, @"0,(\d+):\s*\((\d+),(\d+),(\d+)");
                if (!match.Success) continue;

                var y = int.Parse(match.Groups[1].Value);
                var r = int.Parse(match.Groups[2].Value);
                var g = int.Parse(match.Groups[3].Value);
                var b = int.Parse(match.Groups[4].Value);

                // Handle 16-bit values (ImageMagick 7 may output 0-65535)
                if (r > 255 || g > 255 || b > 255)
                {
                    r = r * 255 / 65535;
                    g = g * 255 / 65535;
                    b = b * 255 / 65535;
                }

                var maxC = Math.Max(r, Math.Max(g, b));
                var minC = Math.Min(r, Math.Min(g, b));
                var saturation = maxC > 0 ? (maxC - minC) / (double)maxC : 0;
                var brightness = (r + g + b) / 3.0;

                // Detect colored pixels with lenient thresholds:
                // 1. Has noticeable color saturation (not white/gray)
                // 2. Or is distinctly non-white (darker than normal table cells)
                var isColored = (saturation > 0.08 && brightness > 30 && brightness < 240)
                             || (brightness < 190 && maxC - minC > 15);

                if (isColored)
                    coloredYs.Add(y);
            }

            if (coloredYs.Count == 0) return bands;

            // Group consecutive colored Y positions into bands
            coloredYs.Sort();
            int bandStart = coloredYs[0];
            int bandEnd = coloredYs[0];

            for (int i = 1; i < coloredYs.Count; i++)
            {
                if (coloredYs[i] <= bandEnd + 3)
                {
                    bandEnd = coloredYs[i];
                }
                else
                {
                    if (bandEnd - bandStart >= 3) // Min 3px band height
                        bands.Add((bandStart, bandEnd));
                    bandStart = coloredYs[i];
                    bandEnd = coloredYs[i];
                }
            }
            if (bandEnd - bandStart >= 3)
                bands.Add((bandStart, bandEnd));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Color band detection failed for strategy");
        }

        return bands;
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
            // Tesseract outputs confidence as float (e.g. "42.886963"), parse as double then round
            if (!double.TryParse(parts[10], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var confD)) continue;
            var conf = (int)Math.Round(confD);

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

    internal Dieta ParseTableFromWords(List<OcrWord> words, int usuarioId, string nombreDieta, string nombreArchivo,
        List<(int top, int bottom)>? colorBands = null)
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
        var mealRows = DetectMealRows(words, dayColumns, colorBands);
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

                _logger.LogInformation("Cell {Day}/{Meal}: {Words} words → {Items} food items",
                    day.Label, meal.MealType, cellWords.Count, foodItems.Count);

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

    internal List<DayColumn> DetectDayColumns(List<OcrWord> words)
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

        // Fallback: detect columns by position if text-based detection failed
        if (columns.Count < 3)
        {
            _logger.LogInformation("Text-based detection found {Count} columns, trying position-based", columns.Count);
            var positionColumns = DetectColumnsByPosition(words);
            if (positionColumns.Count > columns.Count)
                columns = positionColumns;
        }

        if (columns.Count == 0) return columns;

        columns = columns.OrderBy(c => c.HeaderCenterX).ToList();

        // Calculate column boundaries.
        // ContentLeft uses the current column's HeaderLeft (left edge of the header text) so that
        // body text starting at the left edge of the cell is not cut off by a midpoint calculation.
        // A small margin (half the average inter-column gap) is subtracted to also capture words
        // that may be slightly to the left of the header start.
        var imageWidth = words.Max(w => w.Right) + 10;
        var avgColGap = columns.Count > 1
            ? (columns[^1].HeaderCenterX - columns[0].HeaderCenterX) / (columns.Count - 1)
            : imageWidth / (double)columns.Count;
        for (int i = 0; i < columns.Count; i++)
        {
            var headerLeft = columns[i].HeaderLeft > 0 ? columns[i].HeaderLeft : (int)columns[i].HeaderCenterX;
            columns[i].ContentLeft = i == 0
                ? 0
                : Math.Max(0, headerLeft - avgColGap * 0.15);

            columns[i].ContentRight = i == columns.Count - 1
                ? imageWidth
                : (columns[i].HeaderCenterX + columns[i + 1].HeaderCenterX) / 2;
        }

        _logger.LogInformation("Day columns: {Cols}",
            string.Join(", ", columns.Select(c => $"{c.Label} [{c.ContentLeft:F0}-{c.ContentRight:F0}]")));

        return columns;
    }

    internal List<DayColumn> DetectColumnsByPosition(List<OcrWord> words)
    {
        if (words.Count < 7) return new List<DayColumn>();

        var imageWidth = words.Max(w => w.Right) + 10;
        var imageHeight = words.Max(w => w.Bottom) + 10;

        // Find words in the header area (top ~8% of image)
        var headerCutoff = imageHeight * 0.08;
        var headerWords = words.Where(w => w.CenterY < headerCutoff)
            .OrderBy(w => w.CenterX)
            .ToList();

        if (headerWords.Count >= 5)
        {
            var columnCenters = headerWords.Select(w => w.CenterX).ToList();

            // Calculate gaps between consecutive header positions
            var gaps = new List<double>();
            for (int i = 1; i < columnCenters.Count; i++)
                gaps.Add(columnCenters[i] - columnCenters[i - 1]);

            if (gaps.Count >= 4)
            {
                var avgGap = gaps.Average();
                var isRegular = gaps.All(g => Math.Abs(g - avgGap) < avgGap * 0.4);

                if (isRegular)
                {
                    // Extrapolate to 7 columns if we have at least 5
                    var allCenters = new List<double>(columnCenters);
                    while (allCenters.Count < 7)
                    {
                        var nextX = allCenters[^1] + avgGap;
                        if (nextX < imageWidth * 0.98) allCenters.Add(nextX);
                        else break;
                    }
                    allCenters = allCenters.Take(7).ToList();

                    var columns = new List<DayColumn>();
                    var headerTop = (int)headerWords.Average(w => w.Top);

                    for (int i = 0; i < allCenters.Count; i++)
                    {
                        columns.Add(new DayColumn
                        {
                            DayOfWeek = DayOfWeekMap[Math.Min(i, DayOfWeekMap.Length - 1)],
                            Label = $"Día {i + 1}",
                            HeaderCenterX = allCenters[i],
                            HeaderTop = headerTop,
                            HeaderLeft = (int)(allCenters[i] - avgGap / 3),
                            HeaderRight = (int)(allCenters[i] + avgGap / 3)
                        });
                    }

                    _logger.LogInformation("Position-based detection: {Count} columns, avg spacing {Gap:F0}px",
                        columns.Count, avgGap);

                    return columns;
                }
            }
        }

        // Second fallback: cluster ALL words by X position into columns
        // Useful when header words aren't detected but content words form clear columns
        return DetectColumnsByXClustering(words, imageWidth);
    }

    private List<DayColumn> DetectColumnsByXClustering(List<OcrWord> words, int imageWidth)
    {
        // Divide image into N equal buckets and count words per bucket
        // Try 8 buckets (1 label + 7 days) first, then 7
        foreach (var totalCols in new[] { 8, 7 })
        {
            var colWidth = imageWidth / (double)totalCols;
            var buckets = new Dictionary<int, int>();

            foreach (var word in words)
            {
                var bucket = Math.Min((int)(word.CenterX / colWidth), totalCols - 1);
                buckets[bucket] = buckets.GetValueOrDefault(bucket, 0) + 1;
            }

            var avgCount = words.Count / (double)totalCols;
            var significantBuckets = buckets.Where(b => b.Value > avgCount * 0.3)
                .OrderBy(b => b.Key)
                .ToList();

            if (significantBuckets.Count >= 5)
            {
                // Skip leftmost bucket if it's far left (label column)
                var dayBuckets = significantBuckets;
                if (dayBuckets[0].Key == 0 && totalCols == 8)
                    dayBuckets = dayBuckets.Skip(1).ToList();
                dayBuckets = dayBuckets.Take(7).ToList();

                var columns = new List<DayColumn>();
                for (int i = 0; i < dayBuckets.Count; i++)
                {
                    var centerX = (dayBuckets[i].Key + 0.5) * colWidth;
                    columns.Add(new DayColumn
                    {
                        DayOfWeek = DayOfWeekMap[Math.Min(i, DayOfWeekMap.Length - 1)],
                        Label = $"Día {i + 1}",
                        HeaderCenterX = centerX,
                        HeaderTop = 0,
                        HeaderLeft = (int)(centerX - colWidth / 3),
                        HeaderRight = (int)(centerX + colWidth / 3)
                    });
                }

                _logger.LogInformation("X-clustering detection: {Count} columns from {TotalCols}-bucket split",
                    columns.Count, totalCols);
                return columns;
            }
        }

        return new List<DayColumn>();
    }

    /// <summary>
    /// Build MealRow objects from detected color bands.
    /// Two modes:
    /// - Thin bands (≤50px avg): bands are header bars, content is between them.
    /// - Thick bands (>50px avg): bands ARE the content areas (colored cell backgrounds).
    /// </summary>
    private List<MealRow> BuildMealRowsFromColorBands(List<(int top, int bottom)> colorBands, List<OcrWord> words)
    {
        var mealTypes = new[] { TipoComida.Desayuno, TipoComida.Almuerzo, TipoComida.Comida, TipoComida.Merienda, TipoComida.Cena };
        var mealLabels = new[] { "Desayuno", "Tentempié 1", "Comida", "Merienda 1", "Cena" };
        var imageHeight = words.Count > 0 ? words.Max(w => w.Bottom) + 10 : 1300;

        var sortedBands = colorBands.OrderBy(b => b.top).ToList();
        var avgBandHeight = sortedBands.Count > 0 ? sortedBands.Average(b => b.bottom - b.top) : 0;

        List<(int top, int bottom)> mealBands;

        if (avgBandHeight > 50)
        {
            // Thick bands = content cell backgrounds.
            // Each band IS the content area for a meal.
            // Keep bands that actually contain content words.
            mealBands = sortedBands
                .Where(b => words.Any(w => w.CenterY >= b.top && w.CenterY <= b.bottom))
                .ToList();

            _logger.LogInformation("Thick color bands (avg {Avg:F0}px) = content areas. {Count} bands contain words",
                avgBandHeight, mealBands.Count);

            if (mealBands.Count < 4) return new List<MealRow>();
            if (mealBands.Count > 5) mealBands = mealBands.Take(5).ToList();

            var rows = new List<MealRow>();
            for (int i = 0; i < Math.Min(mealBands.Count, mealTypes.Length); i++)
            {
                var band = mealBands[i];
                rows.Add(new MealRow
                {
                    MealType = mealTypes[i],
                    Label = mealLabels[i],
                    Top = band.top,
                    CenterY = (band.top + band.bottom) / 2.0,
                    LabelLeft = 0,
                    LabelRight = 100,
                    ContentTop = band.top,       // Band IS the content
                    ContentBottom = band.bottom
                });
            }

            _logger.LogInformation("Color band meals (content mode): {Meals}",
                string.Join(", ", rows.Select(r => $"{r.MealType} [{r.ContentTop:F0}-{r.ContentBottom:F0}]")));
            return rows;
        }
        else
        {
            // Thin bands = header bars, content is between consecutive header bands.
            var headerThreshold = imageHeight * 0.12;
            mealBands = sortedBands.Where(b => b.top > headerThreshold).ToList();

            if (mealBands.Count < 4)
            {
                _logger.LogInformation("Thin color bands: only {Count} bands below header threshold", mealBands.Count);
                return new List<MealRow>();
            }
            if (mealBands.Count > 5) mealBands = mealBands.Take(5).ToList();

            var rows = new List<MealRow>();
            for (int i = 0; i < Math.Min(mealBands.Count, mealTypes.Length); i++)
            {
                var band = mealBands[i];
                var contentTop = (double)(band.bottom + 1);
                var contentBottom = i < mealBands.Count - 1
                    ? (double)mealBands[i + 1].top
                    : (double)imageHeight;

                rows.Add(new MealRow
                {
                    MealType = mealTypes[i],
                    Label = mealLabels[i],
                    Top = band.top,
                    CenterY = (band.top + band.bottom) / 2.0,
                    LabelLeft = 0,
                    LabelRight = 100,
                    ContentTop = contentTop,
                    ContentBottom = contentBottom
                });
            }

            _logger.LogInformation("Color band meals (header mode): {Meals}",
                string.Join(", ", rows.Select(r => $"{r.MealType} [{r.ContentTop:F0}-{r.ContentBottom:F0}]")));
            return rows;
        }
    }

    internal List<MealRow> DetectMealRows(List<OcrWord> words, List<DayColumn> dayColumns,
        List<(int top, int bottom)>? colorBands = null)
    {
        // Primary strategy: use color band detection (colored horizontal bars = meal headers)
        if (colorBands != null && colorBands.Count >= 4)
        {
            var colorRows = BuildMealRowsFromColorBands(colorBands, words);
            if (colorRows.Count >= 4)
            {
                _logger.LogInformation("Color band meal detection: {Count} meals", colorRows.Count);
                return colorRows;
            }
        }

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

        // Fallback: detect meal rows by Y-position gaps if text-based detection found < 2
        var usedGapDetection = false;
        if (rows.Count < 2)
        {
            _logger.LogInformation("Text-based meal detection found {Count} rows, trying gap-based", rows.Count);
            var gapRows = DetectMealRowsByGaps(words, dayColumns);
            if (gapRows.Count > rows.Count)
            {
                rows = gapRows;
                usedGapDetection = true;
            }
        }

        // Calculate row boundaries (skip if gap-based detection already set them)
        if (!usedGapDetection)
        {
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
        }

        _logger.LogInformation("Meal rows: {Rows}",
            string.Join(", ", rows.Select(r => $"{r.MealType} [{r.ContentTop:F0}-{r.ContentBottom:F0}]")));

        return rows;
    }

    internal List<MealRow> DetectMealRowsByGaps(List<OcrWord> words, List<DayColumn> dayColumns)
    {
        var headerTop = dayColumns.Count > 0 ? dayColumns.Min(c => c.HeaderTop) : 0;
        var imageHeight = words.Max(w => w.Bottom) + 10;

        // Skip past the day header row only (headers are ~40-60px tall)
        var contentStartY = dayColumns.Count > 0
            ? dayColumns.Max(c => c.HeaderTop) + 50
            : (int)(imageHeight * 0.05);
        var contentWords = words.Where(w => w.Top > contentStartY).OrderBy(w => w.Top).ToList();
        if (contentWords.Count < 10) return new List<MealRow>();

        _logger.LogInformation("Gap detection: contentStartY={StartY}, {Count} content words (imageHeight={Height})",
            contentStartY, contentWords.Count, imageHeight);

        List<(int gapTop, int gapBottom, int gapSize)> largeGaps;

        if (dayColumns.Count >= 3)
        {
            // CROSS-COLUMN GAP CONSENSUS: A real meal boundary (teal bar spanning full width)
            // creates a gap in ALL columns. An intra-meal gap only affects 1-2 columns.
            // Collect gaps from each column, cluster by Y-position, score by column consensus.
            var allColumnGaps = new List<(double centerY, int gapSize, int colIdx)>();

            for (int ci = 0; ci < dayColumns.Count; ci++)
            {
                var col = dayColumns[ci];
                var colWords = contentWords
                    .Where(w => w.CenterX >= col.ContentLeft && w.CenterX <= col.ContentRight)
                    .OrderBy(w => w.Top)
                    .ToList();

                if (colWords.Count < 3) continue;

                var gaps = FindYGaps(colWords);
                foreach (var g in gaps)
                    allColumnGaps.Add(((g.gapTop + g.gapBottom) / 2.0, g.gapSize, ci));

                _logger.LogInformation("Column {Col}: {Words} words, {Gaps} gaps, sizes: [{Sizes}]",
                    col.Label, colWords.Count, gaps.Count,
                    string.Join(",", gaps.OrderByDescending(g => g.gapSize).Take(6).Select(g => g.gapSize)));
            }

            // Cluster gaps by Y-center (within tolerance)
            var tolerance = 40.0; // gaps within 40px of each other are clustered
            var clusters = new List<List<(double centerY, int gapSize, int colIdx)>>();

            foreach (var gap in allColumnGaps.OrderBy(g => g.centerY))
            {
                var matched = clusters.FirstOrDefault(c =>
                    Math.Abs(c.Average(g => g.centerY) - gap.centerY) < tolerance);
                if (matched != null)
                    matched.Add(gap);
                else
                    clusters.Add(new List<(double centerY, int gapSize, int colIdx)> { gap });
            }

            // Score each cluster: prefer gaps that appear in MANY columns (real boundaries)
            // over gaps that appear in few columns (intra-meal gaps)
            var scored = clusters.Select(c => new
            {
                CenterY = c.Average(g => g.centerY),
                ColumnCount = c.Select(g => g.colIdx).Distinct().Count(),
                AvgGapSize = c.Average(g => (double)g.gapSize),
                // Score = columns^2 × avgGapSize (heavily weight column consensus)
                Score = Math.Pow(c.Select(g => g.colIdx).Distinct().Count(), 2) * c.Average(g => (double)g.gapSize)
            })
            .OrderByDescending(c => c.Score)
            .ToList();

            _logger.LogInformation("Gap clusters: {Clusters}",
                string.Join("; ", scored.Take(8).Select(c =>
                    $"Y={c.CenterY:F0} cols={c.ColumnCount} avgGap={c.AvgGapSize:F0} score={c.Score:F0}")));

            // "Global best gap" algorithm: iteratively place the highest-scoring
            // gap cluster that validly splits ANY current section.
            // This avoids always targeting the largest section (which may not need splitting).
            var selected = new List<double>();
            var sections = new List<(double start, double end)> { (contentStartY, (double)imageHeight) };
            var minSectionHeight = 40.0;
            var usedClusterYs = new HashSet<double>(); // Track used clusters

            for (int iter = 0; iter < 4 && scored.Count > 0; iter++)
            {
                // Find the globally best gap cluster that validly splits any section
                (int sectionIdx, double clusterY)? bestSplit = null;
                double bestScore = -1;

                foreach (var cluster in scored)
                {
                    if (usedClusterYs.Contains(cluster.CenterY)) continue;

                    // Find which section this cluster falls in
                    for (int si = 0; si < sections.Count; si++)
                    {
                        var sec = sections[si];
                        var margin = Math.Max(15.0, (sec.end - sec.start) * 0.08);

                        if (cluster.CenterY <= sec.start + margin || cluster.CenterY >= sec.end - margin)
                            continue;
                        if (cluster.CenterY - sec.start < minSectionHeight || sec.end - cluster.CenterY < minSectionHeight)
                            continue;

                        // Both halves must contain content words
                        var hasAbove = contentWords.Any(w => w.CenterY >= sec.start && w.CenterY < cluster.CenterY);
                        var hasBelow = contentWords.Any(w => w.CenterY > cluster.CenterY && w.CenterY <= sec.end);
                        if (!hasAbove || !hasBelow) continue;

                        if (cluster.Score > bestScore)
                        {
                            bestScore = cluster.Score;
                            bestSplit = (si, cluster.CenterY);
                        }
                        break; // Cluster can only be in one section
                    }

                    if (bestSplit != null && cluster.Score < bestScore)
                        break; // scored is sorted desc, no better option possible
                }

                if (bestSplit == null) break;

                var splitIdx = bestSplit.Value.sectionIdx;
                var splitY = bestSplit.Value.clusterY;
                selected.Add(splitY);
                usedClusterYs.Add(splitY);

                // Split the section
                var old = sections[splitIdx];
                sections.RemoveAt(splitIdx);
                sections.Insert(splitIdx, (old.start, splitY));
                sections.Insert(splitIdx + 1, (splitY, old.end));
            }

            selected.Sort();
            largeGaps = selected.Select(y => (
                gapTop: (int)(y - 10),
                gapBottom: (int)(y + 10),
                gapSize: 20
            )).ToList();

            _logger.LogInformation("Cross-column consensus: {Count} boundaries at Y=[{Ys}]",
                largeGaps.Count, string.Join(",", selected.Select(y => y.ToString("F0"))));
        }
        else
        {
            // Fallback: single-column or all-words gap detection
            var allGaps = FindYGaps(contentWords);
            if (allGaps.Count == 0) return new List<MealRow>();

            largeGaps = allGaps.OrderByDescending(g => g.gapSize)
                .Take(4)
                .OrderBy(g => g.gapTop)
                .ToList();

            _logger.LogInformation("Fallback gap analysis: {Total} gaps, top4: {Sizes}",
                allGaps.Count, string.Join(",", largeGaps.Select(g => g.gapSize)));
        }

        // Build meal sections from gaps
        var mealTypes = new[] { TipoComida.Desayuno, TipoComida.Almuerzo, TipoComida.Comida, TipoComida.Merienda, TipoComida.Cena };
        var mealLabels = new[] { "Desayuno", "Tentempié 1", "Comida", "Merienda 1", "Cena" };

        var sectionBounds = new List<(double top, double bottom)>();
        double sectionStart = contentWords[0].Top - 5;

        foreach (var gap in largeGaps)
        {
            var gapMid = (gap.gapTop + gap.gapBottom) / 2.0;
            sectionBounds.Add((sectionStart, gapMid));
            sectionStart = gapMid;
        }
        sectionBounds.Add((sectionStart, imageHeight));

        var rows = new List<MealRow>();
        for (int i = 0; i < Math.Min(sectionBounds.Count, mealTypes.Length); i++)
        {
            var (top, bottom) = sectionBounds[i];
            rows.Add(new MealRow
            {
                MealType = mealTypes[i],
                Label = mealLabels[Math.Min(i, mealLabels.Length - 1)],
                Top = (int)top,
                CenterY = (top + bottom) / 2,
                LabelLeft = 0,
                LabelRight = 100,
                ContentTop = top,
                ContentBottom = bottom
            });
        }

        _logger.LogInformation("Gap-based meal detection: {Count} sections", rows.Count);
        return rows;
    }

    /// <summary>
    /// Find Y-gaps between word rows in a list of words (sorted by Top).
    /// Groups words into rows by Y proximity, then finds gaps between rows.
    /// </summary>
    private List<(int gapTop, int gapBottom, int gapSize)> FindYGaps(List<OcrWord> sortedByTop)
    {
        if (sortedByTop.Count < 2) return new List<(int, int, int)>();

        var avgHeight = sortedByTop.Average(w => (double)w.Height);
        // Use avgHeight * 0.8 for same-line grouping (tighter than before)
        var lineThreshold = avgHeight * 0.8;

        // Group words into text lines
        var lineRows = new List<(int top, int bottom)>();
        int curTop = sortedByTop[0].Top;
        int curBottom = sortedByTop[0].Bottom;

        foreach (var word in sortedByTop.Skip(1))
        {
            if (word.Top > curBottom + lineThreshold)
            {
                lineRows.Add((curTop, curBottom));
                curTop = word.Top;
                curBottom = word.Bottom;
            }
            else
            {
                curBottom = Math.Max(curBottom, word.Bottom);
            }
        }
        lineRows.Add((curTop, curBottom));

        // Compute gaps between consecutive text lines
        var gaps = new List<(int gapTop, int gapBottom, int gapSize)>();
        for (int i = 1; i < lineRows.Count; i++)
        {
            var gapSize = lineRows[i].top - lineRows[i - 1].bottom;
            if (gapSize > 0)
                gaps.Add((lineRows[i - 1].bottom, lineRows[i].top, gapSize));
        }

        return gaps;
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

        // Strategy 1: Use Tesseract's own line grouping (BlockNum + LineNum)
        // This is more reliable than Y-gap detection because Tesseract already
        // determined which words belong to the same text line.
        var lineGroups = cellWords
            .GroupBy(w => (w.BlockNum, w.ParNum, w.LineNum))
            .OrderBy(g => g.Min(w => w.Top))
            .Select(g => string.Join(" ", g.OrderBy(w => w.Left).Select(w => w.Text.Trim())))
            .ToList();

        // If Tesseract line grouping gives multiple lines, use it
        if (lineGroups.Count > 1)
        {
            var textLines = BuildTextLines(lineGroups);
            var items = new List<FoodItem>();
            foreach (var line in textLines)
            {
                var food = ParseFoodLine(line);
                if (food != null && food.Name.Length >= 2)
                    items.Add(food);
            }
            items = MergeQuantityOnlyItems(items);
            if (items.Count > 1) return items;
        }

        // Strategy 2: Y-gap based line detection (fallback)
        var sorted = cellWords.OrderBy(w => w.CenterY).ThenBy(w => w.Left).ToList();
        var avgHeight = sorted.Average(w => (double)w.Height);
        var lineBreakThreshold = Math.Max(avgHeight * 0.4, 8);

        var lines = new List<List<OcrWord>>();
        var currentLine = new List<OcrWord> { sorted[0] };

        for (int i = 1; i < sorted.Count; i++)
        {
            var gap = sorted[i].CenterY - sorted[i - 1].CenterY;
            if (gap > lineBreakThreshold)
            {
                lines.Add(currentLine);
                currentLine = new List<OcrWord> { sorted[i] };
            }
            else
            {
                currentLine.Add(sorted[i]);
            }
        }
        lines.Add(currentLine);

        // Strategy 3: If Y-gap gives only 1 line but we have many words,
        // try splitting by colon-quantity patterns (e.g., "300g" followed by uppercase word)
        if (lines.Count <= 1 && cellWords.Count > 6)
        {
            var allText = string.Join(" ", sorted.OrderBy(w => w.Left).Select(w => w.Text.Trim()));
            var splitItems = SplitConcatenatedFoodLine(allText);
            if (splitItems.Count > 1) return splitItems;
        }

        {
            var textLines2 = BuildTextLines(lines
                .Select(l => string.Join(" ", l.OrderBy(w => w.Left).Select(w => w.Text.Trim()))));

            var items2 = new List<FoodItem>();
            foreach (var line in textLines2)
            {
                var food = ParseFoodLine(line);
                if (food != null && food.Name.Length >= 2)
                    items2.Add(food);
            }
            return MergeQuantityOnlyItems(items2);
        }
    }

    /// <summary>
    /// Merges items whose "name" is actually a quantity (starts with a digit) into the
    /// previous item's Quantity field. E.g., Item("BEBIDA DE SOJA", null) + Item("300G (1 TAZA)", null)
    /// → Item("BEBIDA DE SOJA", "300G (1 TAZA)").
    /// </summary>
    private static List<FoodItem> MergeQuantityOnlyItems(List<FoodItem> items)
    {
        if (items.Count <= 1) return items;
        var result = new List<FoodItem>();
        foreach (var item in items)
        {
            // A quantity-only item has a name that starts with a digit
            var isQtyOnly = item.Quantity == null && item.Name.Length > 0 && char.IsDigit(item.Name[0]);
            if (isQtyOnly && result.Count > 0 && result[^1].Quantity == null)
            {
                result[^1] = new FoodItem { Name = result[^1].Name, Quantity = item.Name };
            }
            else
            {
                result.Add(item);
            }
        }
        return result;
    }

    /// <summary>
    /// When all food items are on one line, try to split by quantity patterns.
    /// E.g., "Bebida de soja: 300g proteina de suero: 40g Canela: 3g" →
    /// splits at each "quantity + next word that starts a new food name"
    /// </summary>
    private static List<FoodItem> SplitConcatenatedFoodLine(string text)
    {
        text = FixOcrErrors(text);
        var items = new List<FoodItem>();

        // Split by colon patterns: find "word: qty" segments
        // Pattern: split before a word that is followed by ":" and preceded by a quantity-like token
        var segments = Regex.Split(text,
            @"(?<=\d+\s*[gGkK](?:g|r)?\b|\d+\s*[mM][lL]\b|\(\d[^)]*\))\s+(?=[A-ZÁÉÍÓÚÑ][a-záéíóúñ])");

        if (segments.Length > 1)
        {
            foreach (var seg in segments)
            {
                var food = ParseFoodLine(seg.Trim());
                if (food != null && food.Name.Length >= 2)
                    items.Add(food);
            }
        }

        // Also try splitting on parenthesized groups followed by new word
        if (items.Count <= 1)
        {
            items.Clear();
            var segments2 = Regex.Split(text,
                @"(?<=\))\s+(?=[A-ZÁÉÍÓÚÑ][a-záéíóúñ])");
            if (segments2.Length > 1)
            {
                foreach (var seg in segments2)
                {
                    var food = ParseFoodLine(seg.Trim());
                    if (food != null && food.Name.Length >= 2)
                        items.Add(food);
                }
            }
        }

        return items;
    }

    private static List<string> BuildTextLines(IEnumerable<string> rawLines)
    {
        var textLines = new List<string>();
        foreach (var raw in rawLines)
        {
            var lineText = CleanLine(raw);
            if (string.IsNullOrWhiteSpace(lineText) || lineText.Length < 2) continue;

            if (textLines.Count > 0 && (EndsWithPreposition(textLines[^1]) || textLines[^1].TrimEnd().EndsWith(':')))
            {
                textLines[^1] = textLines[^1] + " " + lineText;
            }
            else
            {
                textLines.Add(lineText);
            }
        }
        return textLines;
    }

    internal static FoodItem? ParseFoodLine(string line)
    {
        line = FixOcrErrors(line);
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

    /// <summary>
    /// Fix common Tesseract OCR errors in food/quantity text.
    /// </summary>
    internal static string FixOcrErrors(string text)
    {
        // "g" misread as "9": 40g→409, 160g→1609, 300g→3009, 3g→39
        text = Regex.Replace(text, @"(\d)9\b", "${1}g");
        // "g" misread as "9" before parenthesis: "409 (2 lonchas)" → "40g (2 lonchas)"
        text = Regex.Replace(text, @"(\d)9(\s*\()", "${1}g${2}");
        // Split stuck-together words at camelCase boundaries: "FILETEDETEMERA" → "FILETE DE TEMERA"
        // Look for lowercase-UPPERCASE or UPPERCASE sequences that should be split
        // Pattern: word chars where two known Spanish words are joined
        // Split at transitions between known food prefixes/suffixes
        text = Regex.Replace(text, @"(?i)(FILETE)(DE)", "$1 $2");
        text = Regex.Replace(text, @"(?i)(HUEVO)(DE)", "$1 $2");
        text = Regex.Replace(text, @"(?i)(PAN)(INTEGRAL)", "$1 $2");
        text = Regex.Replace(text, @"(?i)(SAL)(YODADA)", "$1 $2");
        text = Regex.Replace(text, @"(?i)(BEBIDA)(DE)", "$1 $2");
        text = Regex.Replace(text, @"(?i)(PROTEINA)(DE)", "$1 $2");
        text = Regex.Replace(text, @"(?i)(AGUA)(MINERAL)", "$1 $2");
        text = Regex.Replace(text, @"(?i)(CACAO)(EN)", "$1 $2");
        text = Regex.Replace(text, @"(?i)(ACEITE)(DE)", "$1 $2");
        text = Regex.Replace(text, @"(?i)(CEBOLLA)(BLANCA)", "$1 $2");
        text = Regex.Replace(text, @"(?i)(PIMIENTO)(ROJO|VERDE)", "$1 $2");
        text = Regex.Replace(text, @"(?i)(ALMENDRA)(SIN)", "$1 $2");
        text = Regex.Replace(text, @"(?i)(MANCHEGO)(CORTADAS|CURADO)", "$1 $2");
        return text;
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

    internal class DayColumn
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

    internal class MealRow
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

    internal class FoodItem
    {
        public string Name { get; set; } = "";
        public string? Quantity { get; set; }
    }
}
