namespace EatHealthyCycle.Models;

public class ItemListaCompra
{
    public int Id { get; set; }
    public int PlanSemanalId { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string? Cantidad { get; set; }
    public string? Categoria { get; set; }
    public bool Comprado { get; set; }
    public bool EsManual { get; set; }

    public PlanSemanal PlanSemanal { get; set; } = null!;
}
