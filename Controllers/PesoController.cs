using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EatHealthyCycle.Data;
using EatHealthyCycle.DTOs;
using EatHealthyCycle.Models;

namespace EatHealthyCycle.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class PesoController : ControllerBase
{
    private readonly AppDbContext _db;

    public PesoController(AppDbContext db)
    {
        _db = db;
    }

    [HttpPost("usuarios/{usuarioId}/peso")]
    public async Task<ActionResult<RegistroPesoDto>> Registrar(int usuarioId, CrearRegistroPesoDto dto)
    {
        var usuario = await _db.Usuarios.FindAsync(usuarioId);
        if (usuario == null) return NotFound("Usuario no encontrado");

        var registro = new RegistroPeso
        {
            UsuarioId = usuarioId,
            Fecha = dto.Fecha.Date,
            Peso = dto.Peso,
            Nota = dto.Nota
        };

        _db.RegistrosPeso.Add(registro);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Listar), new { usuarioId },
            new RegistroPesoDto(registro.Id, registro.Fecha, registro.Peso, registro.Nota));
    }

    [HttpGet("usuarios/{usuarioId}/peso")]
    public async Task<ActionResult<List<RegistroPesoDto>>> Listar(int usuarioId, [FromQuery] DateTime? desde, [FromQuery] DateTime? hasta)
    {
        var query = _db.RegistrosPeso.Where(r => r.UsuarioId == usuarioId);

        if (desde.HasValue) query = query.Where(r => r.Fecha >= desde.Value);
        if (hasta.HasValue) query = query.Where(r => r.Fecha <= hasta.Value);

        var registros = await query
            .OrderBy(r => r.Fecha)
            .Select(r => new RegistroPesoDto(r.Id, r.Fecha, r.Peso, r.Nota))
            .ToListAsync();

        return registros;
    }

    [HttpGet("usuarios/{usuarioId}/peso/grafico")]
    public async Task<ActionResult<List<PesoGraficoDto>>> DatosGrafico(int usuarioId)
    {
        var datos = await _db.RegistrosPeso
            .Where(r => r.UsuarioId == usuarioId)
            .OrderBy(r => r.Fecha)
            .Select(r => new PesoGraficoDto(r.Fecha, r.Peso))
            .ToListAsync();

        return datos;
    }

    [HttpDelete("peso/{id}")]
    public async Task<IActionResult> Eliminar(int id)
    {
        var registro = await _db.RegistrosPeso.FindAsync(id);
        if (registro == null) return NotFound();

        _db.RegistrosPeso.Remove(registro);
        await _db.SaveChangesAsync();

        return NoContent();
    }
}
