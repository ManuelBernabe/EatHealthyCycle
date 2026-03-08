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
            alimentos.AddRange(alimentosDb.Select(a => (a.Nombre, a.Cantidad)));
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
                        var match = System.Text.RegularExpressions.Regex.Match(item.Trim(),
                            @"^(.+?)\s*\((.+?)\)\s*$");
                        if (match.Success)
                            alimentos.Add((match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim()));
                        else if (!string.IsNullOrWhiteSpace(item))
                            alimentos.Add((item.Trim(), null));
                    }
                }
            }
        }

        // Group by name
        var agrupados = alimentos
            .GroupBy(a => a.Nombre.ToLowerInvariant())
            .Select(g => new ItemListaCompra
            {
                PlanSemanalId = planSemanalId,
                Nombre = g.First().Nombre,
                Cantidad = string.Join(" + ", g.Where(a => a.Cantidad != null).Select(a => a.Cantidad).Distinct()),
                Categoria = CategorizeFood(g.First().Nombre)
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

    private static string CategorizeFood(string nombre)
    {
        var n = nombre.ToLowerInvariant();

        if (n.Contains("pollo") || n.Contains("ternera") || n.Contains("pavo") ||
            n.Contains("jamon") || n.Contains("jamón") || n.Contains("lomo") ||
            n.Contains("huevo") || n.Contains("clara") || n.Contains("atún") ||
            n.Contains("atun") || n.Contains("serrano") || n.Contains("embuchado"))
            return "Proteínas";

        if (n.Contains("avena") || n.Contains("arroz") || n.Contains("pan ") ||
            n.Contains("pan de molde") || n.Contains("patata") || n.Contains("boniato") ||
            n.Contains("crema de arroz"))
            return "Carbohidratos";

        if (n.Contains("aguacate") || n.Contains("aove") || n.Contains("aceite") ||
            n.Contains("manteca") || n.Contains("cacahuete") || n.Contains("nuez") ||
            n.Contains("almendra"))
            return "Grasas";

        if (n.Contains("calabac") || n.Contains("pimiento") || n.Contains("cebolla") ||
            n.Contains("tomate") || n.Contains("champiñon") || n.Contains("lechuga") ||
            n.Contains("zanahoria"))
            return "Verduras";

        if (n.Contains("whey") || n.Contains("proteina") || n.Contains("proteína") ||
            n.Contains("canela") || n.Contains("cacao"))
            return "Suplementos";

        if (n.Contains("soja") || n.Contains("bebida") || n.Contains("café") ||
            n.Contains("cafe") || n.Contains("leche"))
            return "Bebidas";

        if (n.Contains("sal"))
            return "Condimentos";

        return "Otros";
    }
}
