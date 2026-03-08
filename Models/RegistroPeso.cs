namespace EatHealthyCycle.Models;

public class RegistroPeso
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public DateTime Fecha { get; set; }
    public decimal Peso { get; set; }
    public string? Nota { get; set; }

    public Usuario Usuario { get; set; } = null!;
}
