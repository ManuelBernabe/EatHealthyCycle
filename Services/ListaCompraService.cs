using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using EatHealthyCycle.Data;
using EatHealthyCycle.Models;
using EatHealthyCycle.Services.Interfaces;

namespace EatHealthyCycle.Services;

public class ListaCompraService : IListaCompraService
{
    private readonly AppDbContext _db;

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

        // Strategy 1: Get foods from original diet via ComidaId
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
            alimentos.AddRange(alimentosDb.Select(a => (NormalizeName(a.Nombre), a.Cantidad)));
        }

        // Strategy 2: Parse from PlanComida descriptions if no direct link
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
                        if (match.Success)
                            alimentos.Add((NormalizeName(match.Groups[1].Value), match.Groups[2].Value.Trim()));
                        else if (!string.IsNullOrWhiteSpace(item))
                            alimentos.Add((NormalizeName(item), null));
                    }
                }
            }
        }

        // Group by normalized name, aggregate quantities
        var agrupados = alimentos
            .Where(a => !string.IsNullOrWhiteSpace(a.Nombre) && a.Nombre.Length >= 2)
            .GroupBy(a => NormalizeKey(a.Nombre))
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
    /// Normalize a food name: strip special chars, collapse whitespace.
    /// </summary>
    private static string NormalizeName(string name)
    {
        // Strip box/bullet characters
        name = Regex.Replace(name, @"[\u25A0-\u25FF\u2022\u2023\u25E6\u2043\u2219\u25CB\u25CF\u25A1\u25AA\u25AB□■•●○]", "");
        // Collapse whitespace and trim
        name = Regex.Replace(name, @"\s{2,}", " ").Trim();
        name = name.Trim('-', '–', '—', ' ', '*');
        return name;
    }

    /// <summary>
    /// Create a grouping key: lowercase, no accents aren't stripped but consistent.
    /// </summary>
    private static string NormalizeKey(string name)
    {
        return NormalizeName(name).ToLowerInvariant().Trim();
    }

    /// <summary>
    /// Aggregate quantities: sum numeric values with same units, otherwise join distinct.
    /// Returns weekly total string like "700G" or "7 x 100G".
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
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val))
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
        var n = nombre.ToLowerInvariant();

        if (n.Contains("pollo") || n.Contains("ternera") || n.Contains("pavo") ||
            n.Contains("jamon") || n.Contains("jamón") || n.Contains("lomo") ||
            n.Contains("huevo") || n.Contains("clara") || n.Contains("atún") ||
            n.Contains("atun") || n.Contains("serrano") || n.Contains("embuchado") ||
            n.Contains("salmón") || n.Contains("salmon") || n.Contains("merluza") ||
            n.Contains("gambas") || n.Contains("langostino"))
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
            n.Contains("tomate") || n.Contains("champiñon") || n.Contains("lechuga") ||
            n.Contains("zanahoria") || n.Contains("espinaca") || n.Contains("brócoli") ||
            n.Contains("brocoli") || n.Contains("judías") || n.Contains("judias") ||
            n.Contains("pepino") || n.Contains("espárrago") || n.Contains("esparrago") ||
            n.Contains("berenjena") || n.Contains("col ") || n.Contains("coliflor"))
            return "Verduras";

        if (n.Contains("plátano") || n.Contains("platano") || n.Contains("manzana") ||
            n.Contains("naranja") || n.Contains("fresa") || n.Contains("arándano") ||
            n.Contains("arandano") || n.Contains("fruta") || n.Contains("kiwi") ||
            n.Contains("piña") || n.Contains("melocotón"))
            return "Frutas";

        if (n.Contains("whey") || n.Contains("proteina") || n.Contains("proteína") ||
            n.Contains("canela") || n.Contains("cacao") || n.Contains("creatina") ||
            n.Contains("suplemento"))
            return "Suplementos";

        if (n.Contains("soja") || n.Contains("bebida") || n.Contains("café") ||
            n.Contains("cafe") || n.Contains("leche"))
            return "Bebidas";

        if (n.Contains("sal") || n.Contains("pimienta") || n.Contains("orégano") ||
            n.Contains("oregano") || n.Contains("ajo") || n.Contains("especia") ||
            n.Contains("pimentón"))
            return "Condimentos";

        return "Otros";
    }
}
