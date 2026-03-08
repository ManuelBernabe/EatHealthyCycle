using EatHealthyCycle.Models;

namespace EatHealthyCycle.Services.Interfaces;

public interface IListaCompraService
{
    Task<List<ItemListaCompra>> GenerarListaCompraAsync(int planSemanalId);
}
