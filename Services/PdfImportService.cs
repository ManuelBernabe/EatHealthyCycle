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

    // Maps "DIA 1" -> Monday, "DIA 2" -> Tuesday, etc.
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

    private static readonly Dictionary<string, DayOfWeek> DiasNombreMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lunes"] = DayOfWeek.Monday,
        ["martes"] = DayOfWeek.Tuesday,
        ["miércoles"] = DayOfWeek.Wednesday,
        ["miercoles"] = DayOfWeek.Wednesday,
        ["jueves"] = DayOfWeek.Thursday,
        ["viernes"] = DayOfWeek.Friday,
        ["sábado"] = DayOfWeek.Saturday,
        ["sabado"] = DayOfWeek.Saturday,
        ["domingo"] = DayOfWeek.Sunday
    };

    // Meal type detection - order matters (longer matches first)
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
        var texto = ExtraerTexto(pdfStream);
        _logger.LogInformation("PDF text extracted ({Length} chars): {Preview}",
            texto.Length, texto.Length > 500 ? texto[..500] : texto);

        var dieta = ParsearDieta(texto, usuarioId, nombreDieta, nombreArchivo);

        _logger.LogInformation("Diet parsed: {Days} days, {Meals} total meals",
            dieta.Dias.Count,
            dieta.Dias.Sum(d => d.Comidas.Count));

        _db.Dietas.Add(dieta);
        await _db.SaveChangesAsync();

        return dieta;
    }

    private static string ExtraerTexto(Stream pdfStream)
    {
        using var document = PdfDocument.Open(pdfStream);
        var lines = new List<string>();

        foreach (var page in document.GetPages())
        {
            // Extract words and group by Y position to reconstruct lines
            var words = page.GetWords().ToList();
            if (words.Count == 0)
            {
                // Fallback to page.Text
                if (!string.IsNullOrWhiteSpace(page.Text))
                    lines.Add(page.Text);
                continue;
            }

            // Group words by approximate Y position (same line)
            var wordsByLine = words
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 0))
                .OrderByDescending(g => g.Key); // Top to bottom

            foreach (var lineGroup in wordsByLine)
            {
                var lineWords = lineGroup.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text);
                var line = string.Join(" ", lineWords).Trim();
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }
        }

        return string.Join("\n", lines);
    }

    private static Dieta ParsearDieta(string texto, int usuarioId, string nombreDieta, string nombreArchivo)
    {
        var dieta = new Dieta
        {
            UsuarioId = usuarioId,
            Nombre = nombreDieta,
            ArchivoOriginal = nombreArchivo
        };

        var lineas = texto.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // Filter out noise lines (headers, page numbers, etc.)
        var lineasFiltradas = lineas
            .Where(l => !IsNoiseLine(l))
            .ToList();

        var diasMap = new Dictionary<int, DietaDia>();
        DietaDia? diaActual = null;
        Comida? comidaActual = null;
        int ordenComida = 0;

        foreach (var linea in lineasFiltradas)
        {
            // Detect day: "DIA 1", "DIA 2", "Día 1", etc.
            var diaNum = DetectarDiaNumero(linea);
            if (diaNum.HasValue)
            {
                if (!diasMap.ContainsKey(diaNum.Value))
                {
                    var dayOfWeek = DiaNumeroMap.GetValueOrDefault(diaNum.Value, (DayOfWeek)((diaNum.Value % 7)));
                    var nuevoDia = new DietaDia
                    {
                        DiaSemana = dayOfWeek,
                        Nota = $"Día {diaNum.Value}"
                    };
                    diasMap[diaNum.Value] = nuevoDia;
                    dieta.Dias.Add(nuevoDia);
                }
                diaActual = diasMap[diaNum.Value];
                comidaActual = null;
                ordenComida = diaActual.Comidas.Count;
                continue;
            }

            // Detect named day: "Lunes", "Martes", etc.
            var diaNombre = DetectarDiaNombre(linea);
            if (diaNombre.HasValue)
            {
                var dayIndex = (int)diaNombre.Value;
                if (!diasMap.ContainsKey(dayIndex + 10)) // offset to avoid collision with DIA numbers
                {
                    var nuevoDia = new DietaDia { DiaSemana = diaNombre.Value };
                    diasMap[dayIndex + 10] = nuevoDia;
                    dieta.Dias.Add(nuevoDia);
                }
                diaActual = diasMap[dayIndex + 10];
                comidaActual = null;
                ordenComida = diaActual.Comidas.Count;
                continue;
            }

            // Detect meal type
            var tipoDetectado = DetectarTipoComida(linea);
            if (tipoDetectado.HasValue && diaActual != null)
            {
                // Only create new meal if we don't already have this type for this day
                var existente = diaActual.Comidas.FirstOrDefault(c => c.Tipo == tipoDetectado.Value);
                if (existente != null)
                {
                    comidaActual = existente;
                }
                else
                {
                    comidaActual = new Comida { Tipo = tipoDetectado.Value, Orden = ordenComida++ };
                    diaActual.Comidas.Add(comidaActual);
                }

                // Check if the line also has ingredients after the meal type
                var resto = RemoverTipoComida(linea, tipoDetectado.Value);
                if (!string.IsNullOrWhiteSpace(resto))
                {
                    AgregarAlimentos(comidaActual, resto);
                }
                continue;
            }

            // Add as food item
            if (comidaActual != null && !string.IsNullOrWhiteSpace(linea))
            {
                AgregarAlimentos(comidaActual, linea);
            }
        }

        // Handle "Día off" section - parse as Sunday (day 7)
        ParsearDiaOff(lineasFiltradas, dieta);

        // If no days detected, create generic day
        if (dieta.Dias.Count == 0)
        {
            var diaGenerico = new DietaDia
            {
                DiaSemana = DayOfWeek.Monday,
                Nota = "Importado sin estructura de días detectada"
            };
            var comidaGenerica = new Comida { Tipo = TipoComida.Comida, Orden = 0 };
            foreach (var linea in lineasFiltradas)
            {
                var (nombre, cantidad) = ParsearAlimento(linea);
                if (!string.IsNullOrWhiteSpace(nombre))
                    comidaGenerica.Alimentos.Add(new Alimento { Nombre = nombre, Cantidad = cantidad });
            }
            if (comidaGenerica.Alimentos.Count > 0)
            {
                diaGenerico.Comidas.Add(comidaGenerica);
                dieta.Dias.Add(diaGenerico);
            }
        }

        return dieta;
    }

    private static void ParsearDiaOff(List<string> lineas, Dieta dieta)
    {
        // Look for "Día off" or "dia off" section
        var offIndex = lineas.FindIndex(l =>
            Regex.IsMatch(l, @"d[ií]a\s+off", RegexOptions.IgnoreCase));

        if (offIndex < 0) return;

        // Check if we already have a Sunday
        if (dieta.Dias.Any(d => d.DiaSemana == DayOfWeek.Sunday)) return;

        var diaOff = new DietaDia
        {
            DiaSemana = DayOfWeek.Sunday,
            Nota = "Día de descanso"
        };

        Comida? comidaActual = null;
        int orden = 0;

        for (int i = offIndex + 1; i < lineas.Count; i++)
        {
            var linea = lineas[i];

            // Stop if we hit another section
            if (Regex.IsMatch(linea, @"suplementaci[oó]n", RegexOptions.IgnoreCase))
                break;
            if (DetectarDiaNumero(linea).HasValue)
                break;

            var tipo = DetectarTipoComida(linea);
            if (tipo.HasValue)
            {
                comidaActual = new Comida { Tipo = tipo.Value, Orden = orden++ };
                diaOff.Comidas.Add(comidaActual);

                var resto = RemoverTipoComida(linea, tipo.Value);
                if (!string.IsNullOrWhiteSpace(resto))
                    AgregarAlimentos(comidaActual, resto);
                continue;
            }

            if (comidaActual != null && !string.IsNullOrWhiteSpace(linea))
                AgregarAlimentos(comidaActual, linea);
        }

        if (diaOff.Comidas.Count > 0)
            dieta.Dias.Add(diaOff);
    }

    private static int? DetectarDiaNumero(string linea)
    {
        var match = Regex.Match(linea, @"\bd[ií]a\s+(\d+)\b", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var num) && num >= 1 && num <= 7)
            return num;
        return null;
    }

    private static DayOfWeek? DetectarDiaNombre(string linea)
    {
        var lineaLower = linea.ToLowerInvariant().Trim();
        // Only match if the line is primarily a day name (not embedded in food text)
        foreach (var (nombre, dia) in DiasNombreMap)
        {
            if (Regex.IsMatch(lineaLower, $@"^\s*{Regex.Escape(nombre)}\b"))
                return dia;
        }
        return null;
    }

    private static TipoComida? DetectarTipoComida(string linea)
    {
        var lineaClean = linea.ToLowerInvariant().Trim();
        // Remove bullet points and dashes at start
        lineaClean = Regex.Replace(lineaClean, @"^[\-•\*\s]+", "");

        foreach (var (pattern, tipo) in MealPatterns)
        {
            if (Regex.IsMatch(lineaClean, $@"^{pattern}\b") ||
                Regex.IsMatch(lineaClean, $@"^\s*-?\s*{pattern}\s*:"))
                return tipo;
        }
        return null;
    }

    private static string RemoverTipoComida(string linea, TipoComida tipo)
    {
        // Remove the meal type header from the line to get remaining content
        var cleaned = Regex.Replace(linea, @"^[\-•\*\s]*", "");
        foreach (var (pattern, t) in MealPatterns)
        {
            if (t == tipo)
            {
                cleaned = Regex.Replace(cleaned, $@"^{pattern}\s*:?\s*", "", RegexOptions.IgnoreCase);
                break;
            }
        }
        return cleaned.Trim();
    }

    private static void AgregarAlimentos(Comida comida, string texto)
    {
        // Split by common separators: bullets, "+", line items
        var items = Regex.Split(texto, @"[•\+]|(?<=\b(?:g|ml|kg|unidades?|latas?|rebanadas?))\s*\+?\s*(?=[A-ZÁÉÍÓÚ])");

        foreach (var item in items)
        {
            var cleaned = item.Trim().Trim('-', '•', '*', ' ');
            // Remove leading bullet markers
            cleaned = Regex.Replace(cleaned, @"^[\-•\*\s]+", "").Trim();

            if (string.IsNullOrWhiteSpace(cleaned)) continue;
            if (cleaned.Length < 2) continue;
            if (IsNoiseLine(cleaned)) continue;

            // Skip lines that are just meal type names
            if (DetectarTipoComida(cleaned).HasValue && cleaned.Length < 20) continue;

            var (nombre, cantidad) = ParsearAlimento(cleaned);
            if (!string.IsNullOrWhiteSpace(nombre) && nombre.Length >= 2)
            {
                // Avoid duplicate foods in the same meal
                if (!comida.Alimentos.Any(a =>
                    a.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase)))
                {
                    comida.Alimentos.Add(new Alimento
                    {
                        Nombre = nombre,
                        Cantidad = cantidad
                    });
                }
            }
        }
    }

    private static (string nombre, string? cantidad) ParsearAlimento(string linea)
    {
        linea = linea.Trim().Trim('-', '•', '*', ' ');

        // Pattern: "HARINA DE AVENA 100G" or "HARINA DE AVENA- 100G"
        var matchSuffix = Regex.Match(linea,
            @"^(.+?)\s*[\-–]?\s*(\d+\s*(?:g|gr|mg|kg|ml|l|cl|ud|unidades?|rebanadas?|cucharadas?|vasos?|tazas?|piezas?|latas?))\s*$",
            RegexOptions.IgnoreCase);
        if (matchSuffix.Success)
            return (matchSuffix.Groups[1].Value.Trim(), matchSuffix.Groups[2].Value.Trim());

        // Pattern: "PROTEINA WHEY 20G" - food name with quantity embedded
        var matchEmbedded = Regex.Match(linea,
            @"^(.+?)\s+(\d+\s*(?:G|GR|MG|KG|ML|L|CL))\b(.*)$",
            RegexOptions.IgnoreCase);
        if (matchEmbedded.Success)
        {
            var nombre = matchEmbedded.Groups[1].Value.Trim();
            var cantidad = matchEmbedded.Groups[2].Value.Trim();
            var extra = matchEmbedded.Groups[3].Value.Trim();
            if (!string.IsNullOrWhiteSpace(extra))
                nombre += " " + extra;
            return (nombre, cantidad);
        }

        // Pattern: "2 UNIDADES" or "3 REBANADAS (60G)"
        var matchPrefix = Regex.Match(linea,
            @"^(\d+\s*(?:unidades?|rebanadas?|cucharadas?|latas?|rodajas?))\s+(?:de\s+)?(.+)$",
            RegexOptions.IgnoreCase);
        if (matchPrefix.Success)
            return (matchPrefix.Groups[2].Value.Trim(), matchPrefix.Groups[1].Value.Trim());

        // Pattern with parentheses: "PAN DE MOLDE INTEGRAL 3 REBANADAS (60G)"
        var matchParen = Regex.Match(linea, @"^(.+?)\s*\((.+?)\)\s*$");
        if (matchParen.Success)
            return (matchParen.Groups[1].Value.Trim(), matchParen.Groups[2].Value.Trim());

        return (linea.Trim(), null);
    }

    private static bool IsNoiseLine(string linea)
    {
        var l = linea.Trim().ToLowerInvariant();
        if (l.Length < 2) return true;
        if (Regex.IsMatch(l, @"^(ingesta|ingredientes|rganutri|plan\s+nutricional|cuestionario|gasto\s+energ|necesidades\s+h[ií]dricas|datos\s+antropom|check\s+inicial|fecha|cita|peso|talla|perfil|espalda|manu)\b"))
            return true;
        if (Regex.IsMatch(l, @"^[\d\.\…]+$")) return true; // Page numbers
        if (Regex.IsMatch(l, @"^entrenamiento\s+de\s+", RegexOptions.IgnoreCase)) return true;
        if (l == "café" || l == "cafe") return false; // Keep café as food
        return false;
    }
}
