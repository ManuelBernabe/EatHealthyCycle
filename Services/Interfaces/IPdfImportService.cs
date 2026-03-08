using EatHealthyCycle.Models;

namespace EatHealthyCycle.Services.Interfaces;

public interface IPdfImportService
{
    Task<Dieta> ImportarDietaDesdePdfAsync(int usuarioId, string nombreDieta, Stream pdfStream, string nombreArchivo);
}
