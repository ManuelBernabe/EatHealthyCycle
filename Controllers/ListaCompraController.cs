using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EatHealthyCycle.Data;
using EatHealthyCycle.DTOs;
using EatHealthyCycle.Models;
using EatHealthyCycle.Services.Interfaces;

namespace EatHealthyCycle.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class ListaCompraController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IListaCompraService _listaCompra;
    private readonly IPdfExportService _pdfExport;

    public ListaCompraController(AppDbContext db, IListaCompraService listaCompra, IPdfExportService pdfExport)
    {
        _db = db;
        _listaCompra = listaCompra;
        _pdfExport = pdfExport;
    }

    [HttpPost("planes/{planId}/lista-compra")]
    public async Task<ActionResult<List<ItemListaCompraDto>>> Generar(int planId)
    {
        var plan = await _db.PlanesSemanal.FindAsync(planId);
        if (plan == null) return NotFound();

        var items = await _listaCompra.GenerarListaCompraAsync(planId);
        return items.Select(i => new ItemListaCompraDto(i.Id, i.Nombre, i.Cantidad, i.Categoria, i.Comprado, i.EsManual)).ToList();
    }

    [HttpGet("planes/{planId}/lista-compra")]
    public async Task<ActionResult<List<ItemListaCompraDto>>> Obtener(int planId)
    {
        var items = await _db.ItemsListaCompra
            .Where(i => i.PlanSemanalId == planId)
            .OrderBy(i => i.Categoria)
            .ThenBy(i => i.Nombre)
            .Select(i => new ItemListaCompraDto(i.Id, i.Nombre, i.Cantidad, i.Categoria, i.Comprado, i.EsManual))
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

        return Ok(new ItemListaCompraDto(item.Id, item.Nombre, item.Cantidad, item.Categoria, item.Comprado, item.EsManual));
    }

    [HttpPost("planes/{planId}/lista-compra/item")]
    public async Task<ActionResult<ItemListaCompraDto>> AddItem(int planId, AddItemListaCompraRequest request)
    {
        var plan = await _db.PlanesSemanal.FindAsync(planId);
        if (plan == null) return NotFound();

        var item = new ItemListaCompra
        {
            PlanSemanalId = planId,
            Nombre = request.Nombre,
            Cantidad = request.Cantidad,
            Categoria = request.Categoria ?? "Otros",
            EsManual = true
        };

        _db.ItemsListaCompra.Add(item);
        await _db.SaveChangesAsync();

        return Ok(new ItemListaCompraDto(item.Id, item.Nombre, item.Cantidad, item.Categoria, item.Comprado, item.EsManual));
    }

    [HttpDelete("lista-compra/{itemId}")]
    public async Task<IActionResult> DeleteItem(int itemId)
    {
        var item = await _db.ItemsListaCompra.FindAsync(itemId);
        if (item == null) return NotFound();

        _db.ItemsListaCompra.Remove(item);
        await _db.SaveChangesAsync();

        return NoContent();
    }

    [HttpGet("planes/{planId}/lista-compra/pdf")]
    public async Task<IActionResult> ExportarPdf(int planId)
    {
        var plan = await _db.PlanesSemanal
            .Include(p => p.Dieta)
            .FirstOrDefaultAsync(p => p.Id == planId);
        if (plan == null) return NotFound();

        var items = await _db.ItemsListaCompra
            .Where(i => i.PlanSemanalId == planId)
            .OrderBy(i => i.Categoria)
            .ThenBy(i => i.Nombre)
            .ToListAsync();

        var pdfBytes = _pdfExport.GenerarListaCompraPdf(plan, items);
        return File(pdfBytes, "application/pdf", $"lista-compra-{plan.FechaInicio:yyyy-MM-dd}.pdf");
    }
}
