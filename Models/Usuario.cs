namespace EatHealthyCycle.Models;

public class Usuario
{
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    public List<Dieta> Dietas { get; set; } = new();
    public List<RegistroPeso> RegistrosPeso { get; set; } = new();
    public List<PlanSemanal> PlanesSemanal { get; set; } = new();
}
