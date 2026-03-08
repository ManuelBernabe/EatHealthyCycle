namespace EatHealthyCycle.Models;

public class PlanSemanal
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public int DietaId { get; set; }
    public DateTime FechaInicio { get; set; }
    public DateTime FechaFin { get; set; }
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    public Usuario Usuario { get; set; } = null!;
    public Dieta Dieta { get; set; } = null!;
    public List<PlanDia> Dias { get; set; } = new();
    public List<ItemListaCompra> ItemsListaCompra { get; set; } = new();
}
