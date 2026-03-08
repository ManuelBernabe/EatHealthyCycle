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

    // Meal type words to strip from food names
    private static readonly HashSet<string> MealTypeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "DESAYUNO", "ALMUERZO", "COMIDA", "MERIENDA", "CENA",
        "PRE", "PREDESAYUNO", "MEDIA", "MAÑANA", "MEDIAMANANA"
    };

    // Noise patterns - not real food items
    private static readonly string[] NoisePatterns =
    {
        "masteron", "telmisartan", "ursobilane", "testo cipionato",
        "omega 3", "suplementaci", "actividad f", "día off",
        "dia off", "no entreno", "igual a los", "nac desayuno",
        "lunes jueves", "1ml de dormir", "capsula"
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

        // Strategy 1: Get foods from original diet via ComidaId -> Alimentos table
        var comidaIds = plan.Dias
            .SelectMany(d => d.Comidas)
            .Where(c => c.ComidaId.HasValue)
            .Select(c => c.ComidaId!.Value)
            .Distinct()
            .ToList();

        var alimentos = new List<(string Nombre, string? Cantidad)>();

        if (comidaIds.Count > 0)
        {
            var alimentosDb = await _db.Alimentos
                .Where(a => comidaIds.Contains(a.ComidaId))
                .ToListAsync();
            foreach (var a in alimentosDb)
            {
                var name = CleanFoodName(a.Nombre);
                if (IsGarbageItem(name)) continue;
                alimentos.Add((name, a.Cantidad));
            }
        }

        // Strategy 2: Parse from PlanComida descriptions (comma-separated "Name (Qty)")
        if (alimentos.Count == 0)
        {
            foreach (var dia in plan.Dias)
            {
                foreach (var comida in dia.Comidas)
                {
                    if (string.IsNullOrWhiteSpace(comida.Descripcion) ||
                        comida.Descripcion == "(Sin asignar)") continue;

                    var items = comida.Descripcion.Split(',', StringSplitOptions.TrimEntries);
                    foreach (var item in items)
                    {
                        var match = Regex.Match(item.Trim(), @"^(.+?)\s*\((.+?)\)\s*$");
                        string name;
                        string? qty;
                        if (match.Success)
                        {
                            name = CleanFoodName(match.Groups[1].Value);
                            qty = match.Groups[2].Value.Trim();
                        }
                        else
                        {
                            name = CleanFoodName(item);
                            qty = null;
                        }
                        if (IsGarbageItem(name)) continue;
                        alimentos.Add((name, qty));
                    }
                }
            }
        }

        // Group by normalized key (accent-insensitive, meal-type-stripped), aggregate quantities
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

        // Return all items including manual ones
        return await _db.ItemsListaCompra
            .Where(i => i.PlanSemanalId == planSemanalId)
            .OrderBy(i => i.Categoria)
            .ThenBy(i => i.Nombre)
            .ToListAsync();
    }

    /// <summary>
    /// Clean a food name: whitelist chars, strip trailing meal type words.
    /// </summary>
    private static string CleanFoodName(string name)
    {
        // Step 1: whitelist characters
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '–' ||
                c == '(' || c == ')' || c == '.' || c == ',' || c == '/')
                sb.Append(c);
        }
        name = Regex.Replace(sb.ToString().Trim(), @"\s{2,}", " ");
        name = name.Trim('-', '–', '—', ' ', '*');

        // Step 2: strip trailing meal type words (e.g., "TERNERA CENA" -> "TERNERA")
        name = StripMealTypeSuffix(name);

        return name;
    }

    /// <summary>
    /// Remove trailing meal type words from a food name.
    /// "PECHUGA DE PAVO FRESCO CENA" -> "PECHUGA DE PAVO FRESCO"
    /// "TERNERA COMIDA" -> "TERNERA"
    /// </summary>
    private static string StripMealTypeSuffix(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return name;

        // Strip from end while the last word is a meal type
        int end = words.Length;
        while (end > 1 && MealTypeWords.Contains(words[end - 1]))
            end--;

        if (end == words.Length) return name;
        return string.Join(" ", words.Take(end)).Trim();
    }

    /// <summary>
    /// Check if a food name is garbage (noise, medication, concatenated meals, etc.)
    /// </summary>
    private static bool IsGarbageItem(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2) return true;

        // Too long = likely concatenated multiple foods
        if (name.Length > 45) return true;

        var lower = name.ToLowerInvariant();

        // Contains noise patterns
        foreach (var noise in NoisePatterns)
        {
            if (lower.Contains(noise)) return true;
        }

        // Contains multiple food keywords = likely concatenated
        var foodKeywords = new[] { "pollo", "ternera", "pavo", "arroz", "patata", "boniato",
            "aguacate", "aove", "sal yodada", "champiñon", "pechuga" };
        int hits = foodKeywords.Count(k => lower.Contains(k));
        if (hits >= 3) return true;

        // Starts with quantity (e.g., "50g Proteína whey") — extract the food part
        // This is handled in ParseAlimento instead

        // Pure number or very short
        if (Regex.IsMatch(name, @"^\d+\s*[a-zA-Z]*$") && name.Length < 5) return true;

        return false;
    }

    /// <summary>
    /// Create a grouping key: lowercase, accents stripped for consistent grouping.
    /// "JAMÓN SERRANO" and "JAMON SERRANO" become the same key.
    /// </summary>
    private static string NormalizeKey(string name)
    {
        name = StripMealTypeSuffix(name);
        name = StripAccents(name);
        // Remove embedded quantities for grouping (e.g., "NUEZ 20G" -> "NUEZ")
        name = Regex.Replace(name, @"\s+\d+\s*(g|gr|mg|kg|ml|l|cl|ud|unidades?|rebanadas?)\b.*$",
            "", RegexOptions.IgnoreCase);
        return name.ToLowerInvariant().Trim();
    }

    /// <summary>
    /// Strip accents/diacritics: "JAMÓN" -> "JAMON", "CAFÉ" -> "CAFE"
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
    /// Aggregate quantities: sum numeric values with same units, otherwise join distinct.
    /// </summary>
    private static string AggregateQuantities(List<string> quantities, int occurrences)
    {
        if (quantities.Count == 0) return occurrences > 1 ? $"x{occurrences}" : "";

        // Try to parse all quantities as number + unit
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

        // If all parsed and same unit, sum them
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

        // Fallback: show distinct quantities joined, with count if repeated
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
