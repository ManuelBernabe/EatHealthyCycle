using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EatHealthyCycle.Data;
using EatHealthyCycle.DTOs;
using EatHealthyCycle.Services.Interfaces;

namespace EatHealthyCycle.Controllers;

[ApiController]
[Route("api")]
public class ListaCompraController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IListaCompraService _listaCompra;

    public ListaCompraController(AppDbContext db, IListaCompraService listaCompra)
    {
        _db = db;
        _listaCompra = listaCompra;
    }

    [HttpPost("planes/{planId}/lista-compra")]
    public async Task<ActionResult<List<ItemListaCompraDto>>> Generar(int planId)
    {
        var plan = await _db.PlanesSemanal.FindAsync(planId);
        if (plan == null) return NotFound();

        var items = await _listaCompra.GenerarListaCompraAsync(planId);

        return items.Select(i => new ItemListaCompraDto(i.Id, i.Nombre, i.Cantidad, i.Categoria, i.Comprado)).ToList();
    }

    [HttpGet("planes/{planId}/lista-compra")]
    public async Task<ActionResult<List<ItemListaCompraDto>>> Obtener(int planId)
    {
        var items = await _db.ItemsListaCompra
            .Where(i => i.PlanSemanalId == planId)
            .OrderBy(i => i.Categoria)
            .ThenBy(i => i.Nombre)
            .Select(i => new ItemListaCompraDto(i.Id, i.Nombre, i.Cantidad, i.Categoria, i.Comprado))
            .ToListAsync();

        return items;
    }

    [HttpPut("lista-compra/{itemId}")]
    public async Task<IActionResult> ToggleComprado(int itemId)
    {
        var item = await _db.ItemsListaCompra.FindAsync(itemId);
        if (item == null) return NotFound();

        item.Comprado = !item.Comprado;
        await _db.SaveChangesAsync();

        return Ok(new ItemListaCompraDto(item.Id, item.Nombre, item.Cantidad, item.Categoria, item.Comprado));
    }
}
