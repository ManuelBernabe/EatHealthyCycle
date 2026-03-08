using EatHealthyCycle.Models;

namespace EatHealthyCycle.DTOs;

public record DietaResumenDto(int Id, string Nombre, string? Descripcion, DateTime FechaImportacion, string? ArchivoOriginal);

public record DietaDetalleDto(
    int Id,
    string Nombre,
    string? Descripcion,
    DateTime FechaImportacion,
    List<DietaDiaDto> Dias);

public record DietaDiaDto(int Id, DayOfWeek DiaSemana, string? Nota, List<ComidaDto> Comidas);

public record ComidaDto(int Id, TipoComida Tipo, int Orden, string? Nota, List<AlimentoDto> Alimentos);

public record AlimentoDto(int Id, string Nombre, string? Cantidad, string? Categoria);

public record ActualizarDietaDto(string Nombre, string? Descripcion);
