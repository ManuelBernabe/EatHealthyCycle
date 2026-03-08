using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EatHealthyCycle.Data;
using EatHealthyCycle.DTOs;
using EatHealthyCycle.Services.Interfaces;

namespace EatHealthyCycle.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class PlanesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPlanSemanalService _planService;
    private readonly IPdfExportService _pdfExport;

    public PlanesController(AppDbContext db, IPlanSemanalService planService, IPdfExportService pdfExport)
    {
        _db = db;
        _planService = planService;
        _pdfExport = pdfExport;
    }

    [HttpPost("usuarios/{usuarioId}/planes")]
    public async Task<ActionResult<PlanSemanalResumenDto>> Crear(int usuarioId, CrearPlanSemanalDto dto)
    {
        var usuario = await _db.Usuarios.FindAsync(usuarioId);
        if (usuario == null) return NotFound("Usuario no encontrado");

        var plan = await _planService.GenerarPlanAsync(usuarioId, dto.DietaId, dto.FechaInicio);

        var dieta = await _db.Dietas.FindAsync(dto.DietaId);
        return CreatedAtAction(nameof(ObtenerDetalle), new { id = plan.Id },
            new PlanSemanalResumenDto(plan.Id, plan.DietaId, dieta!.Nombre, plan.FechaInicio, plan.FechaFin));
    }

    [HttpGet("usuarios/{usuarioId}/planes")]
    public async Task<ActionResult<List<PlanSemanalResumenDto>>> Listar(int usuarioId)
    {
        var planes = await _db.PlanesSemanal
            .Where(p => p.UsuarioId == usuarioId)
            .Include(p => p.Dieta)
            .OrderByDescending(p => p.FechaInicio)
            .Select(p => new PlanSemanalResumenDto(p.Id, p.DietaId, p.Dieta.Nombre, p.FechaInicio, p.FechaFin))
            .ToListAsync();

        return planes;
    }

    [HttpGet("planes/{id}")]
    public async Task<ActionResult<PlanSemanalDetalleDto>> ObtenerDetalle(int id)
    {
        var plan = await _db.PlanesSemanal
            .Include(p => p.Dieta)
            .Include(p => p.Dias)
                .ThenInclude(d => d.Comidas)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (plan == null) return NotFound();

        return new PlanSemanalDetalleDto(
            plan.Id,
            plan.Dieta.Nombre,
            plan.FechaInicio,
            plan.FechaFin,
            plan.Dias
                .OrderBy(d => d.DiaSemana == DayOfWeek.Sunday ? 7 : (int)d.DiaSemana)
                .Select(d => new PlanDiaDto(
                    d.Id,
                    d.Fecha,
                    d.DiaSemana,
                    d.Comidas.OrderBy(c => c.Tipo).Select(c =>
                        new PlanComidaDto(c.Id, c.Tipo, c.Descripcion, c.Completada, c.FechaCompletada)
                    ).ToList()
                )).ToList());
    }

    [HttpGet("planes/{id}/pdf")]
    public async Task<IActionResult> ExportarPdf(int id)
    {
        var plan = await _db.PlanesSemanal
            .Include(p => p.Dieta)
            .Include(p => p.Dias)
                .ThenInclude(d => d.Comidas)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (plan == null) return NotFound();

        var pdfBytes = _pdfExport.GenerarPlanSemanalPdf(plan);
        return File(pdfBytes, "application/pdf", $"plan-semanal-{plan.FechaInicio:yyyy-MM-dd}.pdf");
    }

    [HttpPut("plancomidas/{id}/completar")]
    public async Task<IActionResult> ToggleCompletada(int id)
    {
        var comida = await _db.PlanComidas.FindAsync(id);
        if (comida == null) return NotFound();

        comida.Completada = !comida.Completada;
        comida.FechaCompletada = comida.Completada ? DateTime.UtcNow : null;
        await _db.SaveChangesAsync();

        return Ok(new PlanComidaDto(comida.Id, comida.Tipo, comida.Descripcion, comida.Completada, comida.FechaCompletada));
    }

    [HttpGet("planes/{planId}/cumplimiento")]
    public async Task<ActionResult<CumplimientoDto>> ObtenerCumplimiento(int planId)
    {
        var comidas = await _db.PlanComidas
            .Where(c => c.PlanDia.PlanSemanalId == planId)
            .ToListAsync();

        if (!comidas.Any()) return NotFound();

        var total = comidas.Count;
        var completadas = comidas.Count(c => c.Completada);
        var porcentaje = total > 0 ? Math.Round((decimal)completadas / total * 100, 1) : 0;

        return new CumplimientoDto(total, completadas, porcentaje);
    }

    [HttpDelete("planes/{id}")]
    public async Task<IActionResult> Eliminar(int id)
    {
        var plan = await _db.PlanesSemanal.FindAsync(id);
        if (plan == null) return NotFound();

        _db.PlanesSemanal.Remove(plan);
        await _db.SaveChangesAsync();

        return NoContent();
    }
}
