namespace EatHealthyCycle.Models;

public class DietaDia
{
    public int Id { get; set; }
    public int DietaId { get; set; }
    public DayOfWeek DiaSemana { get; set; }
    public string? Nota { get; set; }

    public Dieta Dieta { get; set; } = null!;
    public List<Comida> Comidas { get; set; } = new();
}
