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

        // Eliminar lista anterior si existe
        if (plan.ItemsListaCompra.Any())
        {
            _db.ItemsListaCompra.RemoveRange(plan.ItemsListaCompra);
        }

        // Recopilar todos los alimentos del plan a través de las comidas originales
        var comidaIds = plan.Dias
            .SelectMany(d => d.Comidas)
            .Where(c => c.ComidaId.HasValue)
            .Select(c => c.ComidaId!.Value)
            .Distinct()
            .ToList();

        var alimentos = await _db.Alimentos
            .Where(a => comidaIds.Contains(a.ComidaId))
            .ToListAsync();

        // Agrupar por nombre (case-insensitive) y categoría
        var agrupados = alimentos
            .GroupBy(a => a.Nombre.ToLowerInvariant())
            .Select(g => new ItemListaCompra
            {
                PlanSemanalId = planSemanalId,
                Nombre = g.First().Nombre,
                Cantidad = string.Join(" + ", g.Where(a => a.Cantidad != null).Select(a => a.Cantidad)),
                Categoria = g.FirstOrDefault(a => a.Categoria != null)?.Categoria
            })
            .OrderBy(i => i.Categoria)
            .ThenBy(i => i.Nombre)
            .ToList();

        _db.ItemsListaCompra.AddRange(agrupados);
        await _db.SaveChangesAsync();

        return agrupados;
    }
}
