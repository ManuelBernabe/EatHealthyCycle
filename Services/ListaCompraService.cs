using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using EatHealthyCycle.Data;
using EatHealthyCycle.Models;
using EatHealthyCycle.Services.Interfaces;

namespace EatHealthyCycle.Services;

public class ListaCompraService : IListaCompraService
{
    private readonly AppDbContext _db;

    // Meal type words that should never appear in food names
    private static readonly HashSet<string> MealTypeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "DESAYUNO", "ALMUERZO", "COMIDA", "MERIENDA", "CENA",
        "PRE", "PREDESAYUNO", "MEDIA", "MAÑANA", "MEDIAMANANA"
    };

    // Prepositions that indicate the food name continues on the next comma-separated part
    private static readonly HashSet<string> Prepositions = new(StringComparer.OrdinalIgnoreCase)
    {
        "DE", "DEL", "CON", "AL", "EN", "A", "LA", "LAS", "LOS"
    };

    // Words that are NOT standalone food items (adjectives/modifiers from fragmented names)
    private static readonly HashSet<string> NotStandaloneFood = new(StringComparer.OrdinalIgnoreCase)
    {
        "HERVIDO", "COCIDO", "FRESCO", "INTEGRAL", "NATURAL", "PASTEURIZADA",
        "PELADA", "PELADO", "DESGRASADO", "TOSTADO", "RALLADO",
        "REBANADAS", "UNIDADES", "LATAS", "RODAJAS", "CUCHARADAS",
        "AÑADIR", "GUSTO", "YODADA", "SERRANO", "EMBUCHADO"
    };

    // Noise patterns - not real food items
    private static readonly string[] NoisePatterns =
    {
        "masteron", "telmisartan", "ursobilane", "testo cipionato",
        "omega 3", "suplementaci", "actividad f", "día off",
        "dia off", "no entreno", "igual a los", "nac desayuno",
        "lunes jueves", "1ml de dormir", "capsula", "1ml jueves",
        "lunes 1ml", "de dormir", "cipionato", "enantate"
    };

    public ListaCompraService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<ItemListaCompra>> GenerarListaCompraAsync(int planSemanalId)
    {
        var plan = await _db.PlanesSemanal
            .Include(p => p.Dias)
                .ThenInclude(d => d.Comidas)
            .Include(p => p.ItemsListaCompra)
            .FirstOrDefaultAsync(p => p.Id == planSemanalId)
            ?? throw new InvalidOperationException("Plan semanal no encontrado");

        // Remove existing auto-generated items (keep manually added ones)
        var autoItems = plan.ItemsListaCompra.Where(i => !i.EsManual).ToList();
        if (autoItems.Any())
            _db.ItemsListaCompra.RemoveRange(autoItems);

        // Always parse from Descripcion (comma-separated format built by PlanSemanalService)
        var alimentos = new List<(string Nombre, string? Cantidad)>();

        foreach (var dia in plan.Dias)
        {
            foreach (var comida in dia.Comidas)
            {
                if (string.IsNullOrWhiteSpace(comida.Descripcion) ||
                    comida.Descripcion == "(Sin asignar)") continue;

                var items = ParseDescripcion(comida.Descripcion);
                alimentos.AddRange(items);
            }
        }

        // Group by normalized key (accent-insensitive, cleaned), aggregate quantities
        var agrupados = alimentos
            .Where(a => !string.IsNullOrWhiteSpace(a.Nombre) && a.Nombre.Length >= 2)
            .GroupBy(a => NormalizeKey(a.Nombre))
            .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Key.Length >= 2)
            .Select(g =>
            {
                var quantities = g
                    .Where(a => !string.IsNullOrWhiteSpace(a.Cantidad))
                    .Select(a => a.Cantidad!)
                    .ToList();

                return new ItemListaCompra
                {
                    PlanSemanalId = planSemanalId,
                    Nombre = g.First().Nombre,
                    Cantidad = AggregateQuantities(quantities, g.Count()),
                    Categoria = CategorizeFood(g.First().Nombre)
                };
            })
            .OrderBy(i => i.Categoria)
            .ThenBy(i => i.Nombre)
            .ToList();

        _db.ItemsListaCompra.AddRange(agrupados);
        await _db.SaveChangesAsync();

        return await _db.ItemsListaCompra
            .Where(i => i.PlanSemanalId == planSemanalId)
            .OrderBy(i => i.Categoria)
            .ThenBy(i => i.Nombre)
            .ToListAsync();
    }

    /// <summary>
    /// Parse a Descripcion string into individual food items.
    /// Format: "Food1 (Qty1), Food2 (Qty2), Food3"
    /// Handles fragments by joining items split across commas.
    /// </summary>
    private static List<(string Nombre, string? Cantidad)> ParseDescripcion(string descripcion)
    {
        var result = new List<(string, string?)>();

        // Split by comma
        var parts = descripcion.Split(',', StringSplitOptions.TrimEntries)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        // Join fragments: items ending with prepositions or followed by non-food words
        var joined = JoinFragments(parts);

        foreach (var item in joined)
        {
            var cleaned = CleanFoodName(item);
            if (IsGarbageItem(cleaned)) continue;

            var (nombre, cantidad) = ExtractQuantity(cleaned);
            nombre = nombre.Trim();
            if (!string.IsNullOrWhiteSpace(nombre) && nombre.Length >= 2 && !IsGarbageItem(nombre))
                result.Add((nombre, cantidad));
        }

        return result;
    }

    /// <summary>
    /// Join comma-separated fragments that belong together (both directions).
    /// Forward: "PECHUGA DE, POLLO (200G)" → "PECHUGA DE POLLO (200G)"
    /// Backward: "..., DE MOLDE INTEGRAL" joins with previous item
    /// </summary>
    private static List<string> JoinFragments(List<string> parts)
    {
        if (parts.Count <= 1) return parts;

        // Pass 1: Forward join - items ending with preposition absorb next
        var forward = new List<string>();
        int i = 0;
        while (i < parts.Count)
        {
            var current = parts[i].Trim();
            i++;
            while (i < parts.Count)
            {
                var next = parts[i].Trim();
                if (ShouldJoinForward(current, next))
                {
                    current += " " + next;
                    i++;
                }
                else break;
            }
            forward.Add(current);
        }

        // Pass 2: Backward join - items starting with preposition/article join with previous
        var result = new List<string>();
        foreach (var item in forward)
        {
            var trimmed = item.Trim();
            if (result.Count > 0 && ShouldJoinBackward(trimmed))
            {
                result[^1] = result[^1] + " " + trimmed;
            }
            else
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    private static bool ShouldJoinForward(string current, string next)
    {
        var lastWord = current.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?.ToUpperInvariant() ?? "";
        var nextFirstWord = next.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.ToUpperInvariant() ?? "";

        // Current ends with preposition
        if (Prepositions.Contains(lastWord)) return true;
        // Next starts with modifier/adjective
        if (NotStandaloneFood.Contains(nextFirstWord)) return true;
        // Next is just a number + unit
        if (Regex.IsMatch(next.Trim(), @"^\d+\s*(g|gr|mg|kg|ml|l|cl)\b", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    private static bool ShouldJoinBackward(string item)
    {
        var words = item.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        var first = words[0].ToUpperInvariant();

        // Starts with preposition: "DE MOLDE...", "CON CALCIO...", "AL NATURAL..."
        if (Prepositions.Contains(first)) return true;
        // Starts with standalone modifier: "HERVIDO (200G)", "INTEGRAL 2..."
        if (NotStandaloneFood.Contains(first)) return true;

        return false;
    }

    /// <summary>
    /// Clean a food name: whitelist chars, strip meal type words, strip noise.
    /// </summary>
    private static string CleanFoodName(string name)
    {
        // Step 1: whitelist characters
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '–' ||
                c == '(' || c == ')' || c == '.' || c == '/' || c == ',')
                sb.Append(c);
        }
        name = Regex.Replace(sb.ToString().Trim(), @"\s{2,}", " ");
        name = name.Trim('-', '–', '—', ' ', '*', ',');

        // Step 2: strip meal type words from anywhere in the name
        name = StripMealTypeWords(name);

        // Step 3: strip "AÑADIR AL GUSTO" and similar
        name = Regex.Replace(name, @"\s*a[ñn]adir\s+al\s+gusto\s*", " ", RegexOptions.IgnoreCase).Trim();

        return name.Trim();
    }

    /// <summary>
    /// Remove meal type words from anywhere in the food name.
    /// "PROTEINA DESAYUNO WHEY" → "PROTEINA WHEY"
    /// "SAL YODADA COMIDA" → "SAL YODADA"
    /// </summary>
    private static string StripMealTypeWords(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var filtered = words.Where(w => !MealTypeWords.Contains(w)).ToArray();
        return string.Join(" ", filtered).Trim();
    }

    /// <summary>
    /// Extract quantity from a food item string.
    /// "HARINA DE AVENA (100G)" → ("HARINA DE AVENA", "100G")
    /// "PECHUGA DE POLLO 200G" → ("PECHUGA DE POLLO", "200G")
    /// "2 REBANADAS (60G)" → ("PAN DE MOLDE INTEGRAL", "2 REBANADAS (60G)")
    /// </summary>
    private static (string nombre, string? cantidad) ExtractQuantity(string text)
    {
        text = text.Trim();

        // Pattern 1: "Name (Qty)" — quantity in parentheses at end
        var m1 = Regex.Match(text, @"^(.+?)\s*\((\d+\s*[a-zA-Z]*)\)\s*$");
        if (m1.Success)
            return (m1.Groups[1].Value.Trim(), m1.Groups[2].Value.Trim());

        // Pattern 2: "Name QtyUnit" — quantity at end without parens
        var m2 = Regex.Match(text,
            @"^(.+?)\s+(\d+\s*(?:G|GR|MG|KG|ML|L|CL|UNIDADES?|REBANADAS?|LATAS?))\s*$",
            RegexOptions.IgnoreCase);
        if (m2.Success)
            return (m2.Groups[1].Value.Trim(), m2.Groups[2].Value.Trim());

        // Pattern 3: "Qty Name" — quantity prefix like "2 REBANADAS"
        var m3 = Regex.Match(text,
            @"^(\d+\s*(?:UNIDADES?|REBANADAS?|CUCHARADAS?|LATAS?|RODAJAS?))\s+(?:DE\s+)?(.+)$",
            RegexOptions.IgnoreCase);
        if (m3.Success)
            return (m3.Groups[2].Value.Trim(), m3.Groups[1].Value.Trim());

        // Pattern 4: embedded "QtyUnit" in middle — extract first quantity found
        var m4 = Regex.Match(text,
            @"^(.+?)\s+(\d+\s*(?:G|GR|MG|KG|ML|L|CL))\b",
            RegexOptions.IgnoreCase);
        if (m4.Success)
        {
            var nombre = m4.Groups[1].Value.Trim();
            var cantidad = m4.Groups[2].Value.Trim();
            // Don't include text after the quantity (it's likely a different food from a blob)
            return (nombre, cantidad);
        }

        return (text, null);
    }

    /// <summary>
    /// Check if a food name is garbage (noise, medication, fragments, etc.)
    /// </summary>
    private static bool IsGarbageItem(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2) return true;

        // Too long = likely concatenated multiple foods
        if (name.Length > 50) return true;

        var lower = name.ToLowerInvariant();

        // Noise patterns
        foreach (var noise in NoisePatterns)
        {
            if (lower.Contains(noise)) return true;
        }

        // Contains multiple distinct food keywords = likely concatenated
        var foodKeywords = new[] { "pollo", "ternera", "pavo", "arroz", "patata", "boniato",
            "aguacate", "aove", "sal yodada", "champiñon", "pechuga", "atun", "atún",
            "pan de molde", "huevo", "tortilla" };
        int hits = foodKeywords.Count(k => lower.Contains(k));
        if (hits >= 2) return true;

        // Starts with "-" (medication/supplement prefix from PDF)
        if (name.StartsWith("-")) return true;

        // Just a number or very short meaningless text
        if (Regex.IsMatch(name, @"^\d+\s*[a-zA-Z]{0,2}$")) return true;

        // Common fragments that aren't real food items
        var firstWord = name.Split(' ').First().ToUpperInvariant();
        if (NotStandaloneFood.Contains(name.ToUpperInvariant()) ||
            (name.Split(' ').Length == 1 && NotStandaloneFood.Contains(firstWord)))
            return true;

        // "50g Proteína whey" - starts with quantity (will be handled by the item it was joined from)
        if (Regex.IsMatch(name, @"^\d+\s*(g|ml|mg)\s", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Create a grouping key: lowercase, accents stripped, meal types removed, quantities removed.
    /// </summary>
    private static string NormalizeKey(string name)
    {
        name = StripMealTypeWords(name);
        name = StripAccents(name);
        // Remove quantities for grouping
        name = Regex.Replace(name, @"\s+\d+\s*(g|gr|mg|kg|ml|l|cl|ud|unidades?|rebanadas?)\b.*$",
            "", RegexOptions.IgnoreCase);
        // Remove parenthesized content
        name = Regex.Replace(name, @"\s*\([^)]*\)", "");
        // Remove "1 CUCHARA..." type suffixes
        name = Regex.Replace(name, @"\s+\d+\s+cuchara.*$", "", RegexOptions.IgnoreCase);
        return name.ToLowerInvariant().Trim();
    }

    /// <summary>
    /// Strip accents/diacritics: "JAMÓN" → "JAMON"
    /// </summary>
    private static string StripAccents(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Aggregate quantities: sum numeric values with same units.
    /// </summary>
    private static string AggregateQuantities(List<string> quantities, int occurrences)
    {
        if (quantities.Count == 0) return occurrences > 1 ? $"x{occurrences}" : "";

        var parsed = new List<(decimal value, string unit)>();
        foreach (var q in quantities)
        {
            var m = Regex.Match(q.Trim(), @"^(\d+(?:[.,]\d+)?)\s*([a-zA-Z]+)$");
            if (m.Success && decimal.TryParse(m.Groups[1].Value.Replace(',', '.'),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
            {
                parsed.Add((val, m.Groups[2].Value.ToUpperInvariant()));
            }
        }

        if (parsed.Count == quantities.Count && parsed.Count > 0)
        {
            var byUnit = parsed.GroupBy(p => p.unit);
            if (byUnit.Count() == 1)
            {
                var unit = byUnit.First().Key;
                var total = byUnit.First().Sum(p => p.value);
                return $"{total:0.##}{unit}";
            }
        }

        var distinct = quantities.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = string.Join(" + ", distinct);
        if (occurrences > distinct.Count)
            result += $" (x{occurrences})";
        return result;
    }

    private static string CategorizeFood(string nombre)
    {
        var n = StripAccents(nombre).ToLowerInvariant();

        if (n.Contains("pollo") || n.Contains("ternera") || n.Contains("pavo") ||
            n.Contains("jamon") || n.Contains("lomo") ||
            n.Contains("huevo") || n.Contains("clara") || n.Contains("atun") ||
            n.Contains("serrano") || n.Contains("embuchado") ||
            n.Contains("salmon") || n.Contains("merluza") ||
            n.Contains("gambas") || n.Contains("langostino") || n.Contains("tortilla"))
            return "Proteínas";

        if (n.Contains("avena") || n.Contains("arroz") || n.Contains("pan ") ||
            n.Contains("pan de molde") || n.Contains("patata") || n.Contains("boniato") ||
            n.Contains("crema de arroz") || n.Contains("harina") || n.Contains("pasta") ||
            n.Contains("macarron") || n.Contains("espaguet"))
            return "Carbohidratos";

        if (n.Contains("aguacate") || n.Contains("aove") || n.Contains("aceite") ||
            n.Contains("manteca") || n.Contains("cacahuete") || n.Contains("nuez") ||
            n.Contains("almendra") || n.Contains("frutos secos"))
            return "Grasas";

        if (n.Contains("calabac") || n.Contains("pimiento") || n.Contains("cebolla") ||
            n.Contains("tomate") || n.Contains("champinon") || n.Contains("lechuga") ||
            n.Contains("zanahoria") || n.Contains("espinaca") || n.Contains("brocoli") ||
            n.Contains("judias") || n.Contains("pepino") || n.Contains("esparrago") ||
            n.Contains("berenjena") || n.Contains("col ") || n.Contains("coliflor"))
            return "Verduras";

        if (n.Contains("platano") || n.Contains("manzana") ||
            n.Contains("naranja") || n.Contains("fresa") || n.Contains("arandano") ||
            n.Contains("fruta") || n.Contains("kiwi") ||
            n.Contains("pina") || n.Contains("melocoton"))
            return "Frutas";

        if (n.Contains("whey") || n.Contains("proteina") ||
            n.Contains("canela") || n.Contains("cacao") || n.Contains("creatina") ||
            n.Contains("suplemento"))
            return "Suplementos";

        if (n.Contains("soja") || n.Contains("bebida") || n.Contains("cafe") ||
            n.Contains("leche"))
            return "Bebidas";

        if (n.Contains("sal") || n.Contains("pimienta") || n.Contains("oregano") ||
            n.Contains("ajo") || n.Contains("especia") || n.Contains("pimenton"))
            return "Condimentos";

        return "Otros";
    }
}
