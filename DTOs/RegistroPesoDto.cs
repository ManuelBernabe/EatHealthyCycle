namespace EatHealthyCycle.DTOs;

public record CrearRegistroPesoDto(DateTime Fecha, decimal Peso, string? Nota);

public record RegistroPesoDto(int Id, DateTime Fecha, decimal Peso, string? Nota);

public record PesoGraficoDto(DateTime Fecha, decimal Peso);
