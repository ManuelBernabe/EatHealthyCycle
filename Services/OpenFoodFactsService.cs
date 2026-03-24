using System.Net.Http.Headers;
using System.Text.Json;
using EatHealthyCycle.DTOs;
using EatHealthyCycle.Services.Interfaces;

namespace EatHealthyCycle.Services;

public class OpenFoodFactsService : IOpenFoodFactsService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenFoodFactsService> _logger;

    public OpenFoodFactsService(IHttpClientFactory httpClientFactory, ILogger<OpenFoodFactsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<AlimentoBuscadoDto>> BuscarAlimentosAsync(string termino)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EatHealthyCycle/1.0 (contact@eathealthycycle.app)");

        var url = $"https://world.openfoodfacts.org/cgi/search.pl?search_terms={Uri.EscapeDataString(termino)}&json=true&page_size=15&lc=es&fields=product_name,brands,nutriments";

        try
        {
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var results = new List<AlimentoBuscadoDto>();
            if (!doc.RootElement.TryGetProperty("products", out var products))
                return results;

            foreach (var product in products.EnumerateArray())
            {
                var nombre = GetStringProp(product, "product_name");
                if (string.IsNullOrWhiteSpace(nombre)) continue;

                var marca = GetStringProp(product, "brands");
                int? kcal = null;

                if (product.TryGetProperty("nutriments", out var nutriments))
                {
                    kcal = GetIntProp(nutriments, "energy-kcal_100g")
                        ?? GetIntProp(nutriments, "energy_kcal_100g");
                }

                results.Add(new AlimentoBuscadoDto(nombre, marca, kcal));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error buscando alimentos en Open Food Facts para: {Termino}", termino);
            return new List<AlimentoBuscadoDto>();
        }
    }

    private static string? GetStringProp(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static int? GetIntProp(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val))
            return val;
        if (prop.ValueKind == JsonValueKind.Number)
            return (int)prop.GetDouble();
        return null;
    }
}
