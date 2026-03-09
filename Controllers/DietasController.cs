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
public class DietasController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPdfImportService _pdfImport;
    private readonly IImageImportService _imageImport;

    public DietasController(AppDbContext db, IPdfImportService pdfImport, IImageImportService imageImport)
    {
        _db = db;
        _pdfImport = pdfImport;
        _imageImport = imageImport;
    }

    [HttpPost("usuarios/{usuarioId}/dietas/importar")]
    public async Task<ActionResult<DietaResumenDto>> Importar(int usuarioId, IFormFile archivo, [FromForm] string nombre)
    {
        if (archivo == null || archivo.Length == 0)
            return BadRequest("Debe subir un archivo PDF");

        if (!archivo.ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
            return BadRequest("El archivo debe ser un PDF");

        var usuario = await _db.Usuarios.FindAsync(usuarioId);
        if (usuario == null) return NotFound("Usuario no encontrado");

        using var stream = archivo.OpenReadStream();
        var dieta = await _pdfImport.ImportarDietaDesdePdfAsync(usuarioId, nombre, stream, archivo.FileName);

        return CreatedAtAction(nameof(ObtenerDetalle), new { id = dieta.Id },
            new DietaResumenDto(dieta.Id, dieta.Nombre, dieta.Descripcion, dieta.FechaImportacion, dieta.ArchivoOriginal));
    }

    [HttpGet("usuarios/{usuarioId}/dietas")]
    public async Task<ActionResult<List<DietaResumenDto>>> Listar(int usuarioId)
    {
        var dietas = await _db.Dietas
            .Where(d => d.UsuarioId == usuarioId)
            .OrderByDescending(d => d.FechaImportacion)
            .Select(d => new DietaResumenDto(d.Id, d.Nombre, d.Descripcion, d.FechaImportacion, d.ArchivoOriginal))
            .ToListAsync();

        return dietas;
    }

    [HttpGet("dietas/{id}")]
    public async Task<ActionResult<DietaDetalleDto>> ObtenerDetalle(int id)
    {
        var dieta = await _db.Dietas
            .Include(d => d.Dias)
                .ThenInclude(dd => dd.Comidas)
                    .ThenInclude(c => c.Alimentos)
            .FirstOrDefaultAsync(d => d.Id == id);

        if (dieta == null) return NotFound();

        return new DietaDetalleDto(
            dieta.Id,
            dieta.Nombre,
            dieta.Descripcion,
            dieta.FechaImportacion,
            dieta.Dias.OrderBy(d => d.DiaSemana).Select(dd => new DietaDiaDto(
                dd.Id,
                dd.DiaSemana,
                dd.Nota,
                dd.Comidas.OrderBy(c => c.Orden).Select(c => new ComidaDto(
                    c.Id,
                    c.Tipo,
                    c.Orden,
                    c.Nota,
                    c.Alimentos.Select(a => new AlimentoDto(a.Id, a.Nombre, a.Cantidad, a.Categoria)).ToList()
                )).ToList()
            )).ToList());
    }

    [HttpPut("dietas/{id}")]
    public async Task<IActionResult> Actualizar(int id, ActualizarDietaDto dto)
    {
        var dieta = await _db.Dietas.FindAsync(id);
        if (dieta == null) return NotFound();

        dieta.Nombre = dto.Nombre;
        dieta.Descripcion = dto.Descripcion;
        await _db.SaveChangesAsync();

        return NoContent();
    }

    [HttpPost("usuarios/{usuarioId}/dietas/importar-imagen")]
    public async Task<ActionResult<DietaResumenDto>> ImportarImagen(int usuarioId, IFormFile archivo, [FromForm] string nombre)
    {
        if (archivo == null || archivo.Length == 0)
            return BadRequest("Debe subir una imagen");

        var allowedTypes = new[] { "image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp", "image/bmp" };
        if (!allowedTypes.Any(t => archivo.ContentType.Contains(t.Split('/')[1], StringComparison.OrdinalIgnoreCase)))
            return BadRequest("El archivo debe ser una imagen (PNG, JPG, GIF, WEBP, BMP)");

        var usuario = await _db.Usuarios.FindAsync(usuarioId);
        if (usuario == null) return NotFound("Usuario no encontrado");

        using var stream = archivo.OpenReadStream();
        var dieta = await _imageImport.ImportarDietaDesdeImagenAsync(usuarioId, nombre, stream, archivo.ContentType, archivo.FileName);

        return CreatedAtAction(nameof(ObtenerDetalle), new { id = dieta.Id },
            new DietaResumenDto(dieta.Id, dieta.Nombre, dieta.Descripcion, dieta.FechaImportacion, dieta.ArchivoOriginal));
    }

    [AllowAnonymous]
    [HttpPost("dietas/diagnostico-imagen")]
    public async Task<ActionResult<object>> DiagnosticoImagen(IFormFile archivo)
    {
        if (archivo == null || archivo.Length == 0)
            return BadRequest("Debe subir una imagen");

        using var stream = archivo.OpenReadStream();
        var result = await _imageImport.DiagnosticAnalyzeAsync(stream, archivo.ContentType);
        return Ok(result);
    }

    [HttpDelete("dietas/{id}")]
    public async Task<IActionResult> Eliminar(int id)
    {
        var dieta = await _db.Dietas.FindAsync(id);
        if (dieta == null) return NotFound();

        _db.Dietas.Remove(dieta);
        await _db.SaveChangesAsync();

        return NoContent();
    }
}
