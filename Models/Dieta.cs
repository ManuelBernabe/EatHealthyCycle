namespace EatHealthyCycle.Models;

public class Dieta
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string? Descripcion { get; set; }
    public DateTime FechaImportacion { get; set; } = DateTime.UtcNow;
    public string? ArchivoOriginal { get; set; }

    public Usuario Usuario { get; set; } = null!;
    public List<DietaDia> Dias { get; set; } = new();
}
