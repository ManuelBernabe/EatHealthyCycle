namespace EatHealthyCycle.DTOs;

public record ItemListaCompraDto(int Id, string Nombre, string? Cantidad, string? Categoria, bool Comprado, bool EsManual = false);
public record AddItemListaCompraRequest(string Nombre, string? Cantidad = null, string? Categoria = null);
