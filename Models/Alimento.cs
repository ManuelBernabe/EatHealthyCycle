namespace EatHealthyCycle.Models;

public class Alimento
{
    public int Id { get; set; }
    public int ComidaId { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string? Cantidad { get; set; }
    public string? Categoria { get; set; }
    public int? Kcal { get; set; }

    public Comida Comida { get; set; } = null!;
}
