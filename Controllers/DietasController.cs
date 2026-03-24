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
                    c.Alimentos.Select(a => new AlimentoDto(a.Id, a.Nombre, a.Cantidad, a.Categoria, a.Kcal)).ToList()
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

    // --- Creación manual de dietas ---

    [HttpPost("usuarios/{usuarioId}/dietas/manual")]
    public async Task<ActionResult<DietaResumenDto>> CrearManual(int usuarioId, CrearDietaManualDto dto)
    {
        var usuario = await _db.Usuarios.FindAsync(usuarioId);
        if (usuario == null) return NotFound("Usuario no encontrado");

        var dieta = new Models.Dieta
        {
            UsuarioId = usuarioId,
            Nombre = dto.Nombre,
            Descripcion = dto.Descripcion,
            FechaImportacion = DateTime.UtcNow,
            ArchivoOriginal = null,
            Dias = dto.Dias.Select(d => new Models.DietaDia
            {
                DiaSemana = d.DiaSemana,
                Nota = d.Nota,
                Comidas = d.Comidas.Select(c => new Models.Comida
                {
                    Tipo = c.Tipo,
                    Orden = c.Orden,
                    Nota = c.Nota,
                    Alimentos = c.Alimentos.Select(a => new Models.Alimento
                    {
                        Nombre = a.Nombre,
                        Cantidad = a.Cantidad,
                        Categoria = a.Categoria,
                        Kcal = a.Kcal
                    }).ToList()
                }).ToList()
            }).ToList()
        };

        _db.Dietas.Add(dieta);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(ObtenerDetalle), new { id = dieta.Id },
            new DietaResumenDto(dieta.Id, dieta.Nombre, dieta.Descripcion, dieta.FechaImportacion, dieta.ArchivoOriginal));
    }

    // --- CRUD individual de alimentos ---

    [HttpPost("comidas/{comidaId}/alimentos")]
    public async Task<ActionResult<AlimentoDto>> AgregarAlimento(int comidaId, CrearAlimentoDto dto)
    {
        var comida = await _db.Comidas.FindAsync(comidaId);
        if (comida == null) return NotFound("Comida no encontrada");

        var alimento = new Models.Alimento
        {
            ComidaId = comidaId,
            Nombre = dto.Nombre,
            Cantidad = dto.Cantidad,
            Categoria = dto.Categoria,
            Kcal = dto.Kcal
        };
        _db.Alimentos.Add(alimento);
        await _db.SaveChangesAsync();

        return new AlimentoDto(alimento.Id, alimento.Nombre, alimento.Cantidad, alimento.Categoria, alimento.Kcal);
    }

    [HttpPut("alimentos/{id}")]
    public async Task<IActionResult> ActualizarAlimento(int id, CrearAlimentoDto dto)
    {
        var alimento = await _db.Alimentos.FindAsync(id);
        if (alimento == null) return NotFound();

        alimento.Nombre = dto.Nombre;
        alimento.Cantidad = dto.Cantidad;
        alimento.Categoria = dto.Categoria;
        alimento.Kcal = dto.Kcal;
        await _db.SaveChangesAsync();

        return NoContent();
    }

    [HttpDelete("alimentos/{id}")]
    public async Task<IActionResult> EliminarAlimento(int id)
    {
        var alimento = await _db.Alimentos.FindAsync(id);
        if (alimento == null) return NotFound();

        _db.Alimentos.Remove(alimento);
        await _db.SaveChangesAsync();

        return NoContent();
    }
}
