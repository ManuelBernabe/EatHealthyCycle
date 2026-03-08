using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EatHealthyCycle.Data;
using EatHealthyCycle.DTOs;
using EatHealthyCycle.Models;

namespace EatHealthyCycle.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsuariosController : ControllerBase
{
    private readonly AppDbContext _db;

    public UsuariosController(AppDbContext db)
    {
        _db = db;
    }

    [HttpPost]
    public async Task<ActionResult<UsuarioDto>> Crear(CrearUsuarioDto dto)
    {
        var usuario = new Usuario { Nombre = dto.Nombre, Email = dto.Email };
        _db.Usuarios.Add(usuario);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Obtener), new { id = usuario.Id },
            new UsuarioDto(usuario.Id, usuario.Nombre, usuario.Email, usuario.FechaCreacion));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<UsuarioDto>> Obtener(int id)
    {
        var u = await _db.Usuarios.FindAsync(id);
        if (u == null) return NotFound();
        return new UsuarioDto(u.Id, u.Nombre, u.Email, u.FechaCreacion);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Actualizar(int id, CrearUsuarioDto dto)
    {
        var u = await _db.Usuarios.FindAsync(id);
        if (u == null) return NotFound();

        u.Nombre = dto.Nombre;
        u.Email = dto.Email;
        await _db.SaveChangesAsync();

        return NoContent();
    }
}
