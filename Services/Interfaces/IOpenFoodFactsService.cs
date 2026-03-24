using EatHealthyCycle.DTOs;

namespace EatHealthyCycle.Services.Interfaces;

public interface IOpenFoodFactsService
{
    Task<List<AlimentoBuscadoDto>> BuscarAlimentosAsync(string termino);
}
