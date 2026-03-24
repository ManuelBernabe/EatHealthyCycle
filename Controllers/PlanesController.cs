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

        // Check for duplicate week
        var lunes = dto.FechaInicio;
        while (lunes.DayOfWeek != DayOfWeek.Monday) lunes = lunes.AddDays(-1);
        var yaExiste = await _db.PlanesSemanal.AnyAsync(p => p.UsuarioId == usuarioId && p.FechaInicio == lunes);
        if (yaExiste) return Conflict("Ya existe un plan para esa semana");

        var plan = await _planService.GenerarPlanAsync(usuarioId, dto.DietaId, dto.FechaInicio);
        var dieta = await _db.Dietas.FindAsync(dto.DietaId);

        return CreatedAtAction(nameof(ObtenerDetalle), new { id = plan.Id },
            new PlanSemanalResumenDto(plan.Id, plan.DietaId, dieta?.Nombre ?? "Plan manual", plan.FechaInicio, plan.FechaFin));
    }

    [HttpPost("usuarios/{usuarioId}/planes/manual")]
    public async Task<ActionResult<PlanSemanalResumenDto>> CrearManual(int usuarioId, CrearPlanManualDto dto)
    {
        var usuario = await _db.Usuarios.FindAsync(usuarioId);
        if (usuario == null) return NotFound("Usuario no encontrado");

        var fechaInicio = dto.FechaInicio;
        while (fechaInicio.DayOfWeek != DayOfWeek.Monday)
            fechaInicio = fechaInicio.AddDays(-1);

        var yaExiste = await _db.PlanesSemanal.AnyAsync(p => p.UsuarioId == usuarioId && p.FechaInicio == fechaInicio);
        if (yaExiste) return Conflict("Ya existe un plan para esa semana");

        var plan = new PlanSemanal
        {
            UsuarioId = usuarioId,
            DietaId = null,
            FechaInicio = fechaInicio,
            FechaFin = fechaInicio.AddDays(6)
        };

        for (int i = 0; i < 7; i++)
        {
            var fecha = fechaInicio.AddDays(i);
            plan.Dias.Add(new PlanDia
            {
                Fecha = fecha,
                DiaSemana = fecha.DayOfWeek
            });
        }

        _db.PlanesSemanal.Add(plan);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(ObtenerDetalle), new { id = plan.Id },
            new PlanSemanalResumenDto(plan.Id, null, dto.Nombre, plan.FechaInicio, plan.FechaFin));
    }

    [HttpPost("plandia/{planDiaId}/comidas")]
    public async Task<ActionResult<PlanComidaDto>> AddComida(int planDiaId, AddPlanComidaDto dto)
    {
        var planDia = await _db.Set<PlanDia>().FindAsync(planDiaId);
        if (planDia == null) return NotFound();

        var comida = new PlanComida
        {
            PlanDiaId = planDiaId,
            Tipo = dto.Tipo,
            Descripcion = dto.Descripcion
        };

        _db.PlanComidas.Add(comida);
        await _db.SaveChangesAsync();

        return Ok(new PlanComidaDto(comida.Id, comida.Tipo, comida.Descripcion, comida.Completada, comida.FechaCompletada));
    }

    [HttpDelete("plancomidas/{id}")]
    public async Task<IActionResult> EliminarComida(int id)
    {
        var comida = await _db.PlanComidas.FindAsync(id);
        if (comida == null) return NotFound();

        _db.PlanComidas.Remove(comida);
        await _db.SaveChangesAsync();

        return NoContent();
    }

    [HttpGet("usuarios/{usuarioId}/planes")]
    public async Task<ActionResult<List<PlanSemanalResumenDto>>> Listar(int usuarioId)
    {
        var planes = await _db.PlanesSemanal
            .Where(p => p.UsuarioId == usuarioId)
            .Include(p => p.Dieta)
            .OrderByDescending(p => p.FechaInicio)
            .Select(p => new PlanSemanalResumenDto(p.Id, p.DietaId, p.Dieta != null ? p.Dieta.Nombre : "Plan manual", p.FechaInicio, p.FechaFin))
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
            plan.DietaId,
            plan.Dieta?.Nombre ?? "Plan manual",
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
        var plan = await _db.PlanesSemanal
            .Include(p => p.Dias)
                .ThenInclude(d => d.Comidas)
            .Include(p => p.ItemsListaCompra)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null) return NotFound();

        _db.PlanesSemanal.Remove(plan);
        await _db.SaveChangesAsync();

        return NoContent();
    }
}
