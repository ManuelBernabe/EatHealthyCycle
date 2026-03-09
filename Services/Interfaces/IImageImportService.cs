using EatHealthyCycle.Models;

namespace EatHealthyCycle.Services.Interfaces;

public interface IImageImportService
{
    Task<Dieta> ImportarDietaDesdeImagenAsync(int usuarioId, string nombreDieta, Stream imageStream, string contentType, string nombreArchivo);
    Task<object> DiagnosticAnalyzeAsync(Stream imageStream, string contentType);
}
