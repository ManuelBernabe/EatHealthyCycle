namespace EatHealthyCycle.Models;

public class PlanDia
{
    public int Id { get; set; }
    public int PlanSemanalId { get; set; }
    public DateTime Fecha { get; set; }
    public DayOfWeek DiaSemana { get; set; }

    public PlanSemanal PlanSemanal { get; set; } = null!;
    public List<PlanComida> Comidas { get; set; } = new();
}
