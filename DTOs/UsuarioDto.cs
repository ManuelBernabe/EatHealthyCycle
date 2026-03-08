namespace EatHealthyCycle.DTOs;

public record CrearUsuarioDto(string Nombre, string Email);

public record UsuarioDto(int Id, string Nombre, string Email, DateTime FechaCreacion);
