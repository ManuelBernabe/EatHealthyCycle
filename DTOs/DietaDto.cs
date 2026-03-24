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

public record AlimentoDto(int Id, string Nombre, string? Cantidad, string? Categoria, int? Kcal);

public record ActualizarDietaDto(string Nombre, string? Descripcion);

// --- DTOs para creación manual de dietas ---
public record CrearAlimentoDto(string Nombre, string? Cantidad, string? Categoria, int? Kcal);
public record CrearComidaDto(TipoComida Tipo, int Orden, string? Nota, List<CrearAlimentoDto> Alimentos);
public record CrearDietaDiaDto(DayOfWeek DiaSemana, string? Nota, List<CrearComidaDto> Comidas);
public record CrearDietaManualDto(string Nombre, string? Descripcion, List<CrearDietaDiaDto> Dias);

// --- DTO para búsqueda en Open Food Facts ---
public record AlimentoBuscadoDto(string Nombre, string? Marca, int? KcalPor100g);
