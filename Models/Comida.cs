namespace EatHealthyCycle.Models;

public class Comida
{
    public int Id { get; set; }
    public int DietaDiaId { get; set; }
    public TipoComida Tipo { get; set; }
    public int Orden { get; set; }
    public string? Nota { get; set; }

    public DietaDia DietaDia { get; set; } = null!;
    public List<Alimento> Alimentos { get; set; } = new();
}
