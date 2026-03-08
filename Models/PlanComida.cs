namespace EatHealthyCycle.Models;

public class PlanComida
{
    public int Id { get; set; }
    public int PlanDiaId { get; set; }
    public int? ComidaId { get; set; }
    public TipoComida Tipo { get; set; }
    public string Descripcion { get; set; } = string.Empty;
    public bool Completada { get; set; }
    public DateTime? FechaCompletada { get; set; }

    public PlanDia PlanDia { get; set; } = null!;
    public Comida? Comida { get; set; }
}
