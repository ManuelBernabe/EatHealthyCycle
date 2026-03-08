using System.Text.RegularExpressions;
using EatHealthyCycle.Data;
using EatHealthyCycle.Models;
using EatHealthyCycle.Services.Interfaces;
using UglyToad.PdfPig;

namespace EatHealthyCycle.Services;

public class PdfImportService : IPdfImportService
{
    private readonly AppDbContext _db;

    private static readonly Dictionary<string, DayOfWeek> DiasMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lunes"] = DayOfWeek.Monday,
        ["martes"] = DayOfWeek.Tuesday,
        ["miércoles"] = DayOfWeek.Wednesday,
        ["miercoles"] = DayOfWeek.Wednesday,
        ["jueves"] = DayOfWeek.Thursday,
        ["viernes"] = DayOfWeek.Friday,
        ["sábado"] = DayOfWeek.Saturday,
        ["sabado"] = DayOfWeek.Saturday,
        ["domingo"] = DayOfWeek.Sunday
    };

    private static readonly Dictionary<string, TipoComida> ComidasMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["desayuno"] = TipoComida.Desayuno,
        ["media mañana"] = TipoComida.MediaManana,
        ["media manana"] = TipoComida.MediaManana,
        ["almuerzo"] = TipoComida.Almuerzo,
        ["comida"] = TipoComida.Almuerzo,
        ["merienda"] = TipoComida.Merienda,
        ["cena"] = TipoComida.Cena
    };

    public PdfImportService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Dieta> ImportarDietaDesdePdfAsync(int usuarioId, string nombreDieta, Stream pdfStream, string nombreArchivo)
    {
        var texto = ExtraerTexto(pdfStream);
        var dieta = ParsearDieta(texto, usuarioId, nombreDieta, nombreArchivo);

        _db.Dietas.Add(dieta);
        await _db.SaveChangesAsync();

        return dieta;
    }

    private static string ExtraerTexto(Stream pdfStream)
    {
        using var document = PdfDocument.Open(pdfStream);
        var textoCompleto = string.Join("\n",
            document.GetPages().Select(p => p.Text));
        return textoCompleto;
    }

    private static Dieta ParsearDieta(string texto, int usuarioId, string nombreDieta, string nombreArchivo)
    {
        var dieta = new Dieta
        {
            UsuarioId = usuarioId,
            Nombre = nombreDieta,
            ArchivoOriginal = nombreArchivo
        };

        var lineas = texto.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        DietaDia? diaActual = null;
        Comida? comidaActual = null;
        int ordenComida = 0;

        foreach (var linea in lineas)
        {
            // Detectar día de la semana
            var diaDetectado = DetectarDia(linea);
            if (diaDetectado.HasValue)
            {
                diaActual = new DietaDia { DiaSemana = diaDetectado.Value };
                dieta.Dias.Add(diaActual);
                comidaActual = null;
                ordenComida = 0;
                continue;
            }

            // Detectar tipo de comida
            var tipoDetectado = DetectarTipoComida(linea);
            if (tipoDetectado.HasValue && diaActual != null)
            {
                comidaActual = new Comida { Tipo = tipoDetectado.Value, Orden = ordenComida++ };
                diaActual.Comidas.Add(comidaActual);
                continue;
            }

            // Si hay comida activa, añadir como alimento
            if (comidaActual != null && !string.IsNullOrWhiteSpace(linea))
            {
                var (nombre, cantidad) = ParsearAlimento(linea);
                comidaActual.Alimentos.Add(new Alimento
                {
                    Nombre = nombre,
                    Cantidad = cantidad
                });
            }
        }

        // Si no se detectó ningún día, crear un día genérico con todo el contenido
        if (dieta.Dias.Count == 0)
        {
            var diaGenerico = new DietaDia { DiaSemana = DayOfWeek.Monday, Nota = "Importado sin estructura de días detectada" };
            var comidaGenerica = new Comida { Tipo = TipoComida.Almuerzo, Orden = 0 };
            foreach (var linea in lineas.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                comidaGenerica.Alimentos.Add(new Alimento { Nombre = linea });
            }
            diaGenerico.Comidas.Add(comidaGenerica);
            dieta.Dias.Add(diaGenerico);
        }

        return dieta;
    }

    private static DayOfWeek? DetectarDia(string linea)
    {
        var lineaLower = linea.ToLowerInvariant().Trim();
        foreach (var (palabra, dia) in DiasMap)
        {
            if (Regex.IsMatch(lineaLower, $@"\b{Regex.Escape(palabra)}\b"))
                return dia;
        }
        return null;
    }

    private static TipoComida? DetectarTipoComida(string linea)
    {
        var lineaLower = linea.ToLowerInvariant().Trim();
        foreach (var (palabra, tipo) in ComidasMap)
        {
            if (Regex.IsMatch(lineaLower, $@"\b{Regex.Escape(palabra)}\b"))
                return tipo;
        }
        return null;
    }

    private static (string nombre, string? cantidad) ParsearAlimento(string linea)
    {
        // Intenta extraer cantidad con patrón: "100g de arroz" o "2 rebanadas pan integral"
        var match = Regex.Match(linea, @"^(\d+\s*(?:g|gr|mg|kg|ml|l|cl|ud|unidades?|rebanadas?|cucharadas?|vasos?|tazas?|piezas?)?)\s+(?:de\s+)?(.+)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return (match.Groups[2].Value.Trim(), match.Groups[1].Value.Trim());
        }

        // Patrón: "Arroz (100g)" o "Pan integral - 2 rebanadas"
        var matchParentesis = Regex.Match(linea, @"^(.+?)\s*[\(\-]\s*(.+?)[\)\s]*$");
        if (matchParentesis.Success)
        {
            return (matchParentesis.Groups[1].Value.Trim(), matchParentesis.Groups[2].Value.Trim());
        }

        return (linea.Trim(), null);
    }
}
