using System.Net;
using System.Text;
using System.Text.Json;
using EatHealthyCycle.Controllers;
using EatHealthyCycle.Data;
using EatHealthyCycle.DTOs;
using EatHealthyCycle.Models;
using EatHealthyCycle.Services;
using EatHealthyCycle.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace EatHealthyCycle.Tests;

// ══════════════════════════════════════════════════════
//  OpenFoodFactsService — JSON parsing & fallback
// ══════════════════════════════════════════════════════

public class OpenFoodFactsServiceTests
{
    private readonly Mock<ILogger<OpenFoodFactsService>> _loggerMock = new();

    private OpenFoodFactsService CreateService(HttpResponseMessage response)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        var client = new HttpClient(handler.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        return new OpenFoodFactsService(factory.Object, _loggerMock.Object);
    }

    private static HttpResponseMessage JsonResponse(object data)
    {
        var json = JsonSerializer.Serialize(data);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task BuscarAlimentos_ParsesProductsCorrectly()
    {
        var response = JsonResponse(new
        {
            count = 2,
            products = new[]
            {
                new
                {
                    product_name = "Manzana Golden",
                    brands = "Hacendado",
                    nutriments = new Dictionary<string, object>
                    {
                        ["energy-kcal_100g"] = 52
                    }
                },
                new
                {
                    product_name = "Zumo de manzana",
                    brands = (string)null!,
                    nutriments = new Dictionary<string, object>
                    {
                        ["energy-kcal_100g"] = 46
                    }
                }
            }
        });

        var service = CreateService(response);
        var results = await service.BuscarAlimentosAsync("manzana");

        Assert.Equal(2, results.Count);
        Assert.Equal("Manzana Golden", results[0].Nombre);
        Assert.Equal("Hacendado", results[0].Marca);
        Assert.Equal(52, results[0].KcalPor100g);
        Assert.Equal("Zumo de manzana", results[1].Nombre);
        Assert.Null(results[1].Marca);
        Assert.Equal(46, results[1].KcalPor100g);
    }

    [Fact]
    public async Task BuscarAlimentos_SkipsProductsWithoutName()
    {
        var response = JsonResponse(new
        {
            products = new object[]
            {
                new { product_name = "", brands = "X", nutriments = new Dictionary<string, object>() },
                new { product_name = "Pollo", brands = "Carrefour", nutriments = new Dictionary<string, object> { ["energy-kcal_100g"] = 120 } }
            }
        });

        var service = CreateService(response);
        var results = await service.BuscarAlimentosAsync("pollo");

        Assert.Single(results);
        Assert.Equal("Pollo", results[0].Nombre);
    }

    [Fact]
    public async Task BuscarAlimentos_HandlesNoNutriments()
    {
        var response = JsonResponse(new
        {
            products = new[]
            {
                new { product_name = "Agua mineral", brands = "Bezoya" }
            }
        });

        var service = CreateService(response);
        var results = await service.BuscarAlimentosAsync("agua");

        Assert.Single(results);
        Assert.Equal("Agua mineral", results[0].Nombre);
        Assert.Null(results[0].KcalPor100g);
    }

    [Fact]
    public async Task BuscarAlimentos_HandlesEmptyProducts()
    {
        var response = JsonResponse(new { products = Array.Empty<object>() });

        var service = CreateService(response);
        var results = await service.BuscarAlimentosAsync("xyznoexiste");

        Assert.Empty(results);
    }

    [Fact]
    public async Task BuscarAlimentos_HandlesNoProductsKey()
    {
        var response = JsonResponse(new { count = 0 });

        var service = CreateService(response);
        var results = await service.BuscarAlimentosAsync("nada");

        Assert.Empty(results);
    }

    [Fact]
    public async Task BuscarAlimentos_HandlesFallbackKcalField()
    {
        // Some products use energy_kcal_100g (underscore) instead of energy-kcal_100g (hyphen)
        var response = JsonResponse(new
        {
            products = new[]
            {
                new
                {
                    product_name = "Arroz blanco",
                    brands = "SOS",
                    nutriments = new Dictionary<string, object>
                    {
                        ["energy_kcal_100g"] = 350
                    }
                }
            }
        });

        var service = CreateService(response);
        var results = await service.BuscarAlimentosAsync("arroz");

        Assert.Single(results);
        Assert.Equal(350, results[0].KcalPor100g);
    }

    [Fact]
    public async Task BuscarAlimentos_HandlesFloatKcalValues()
    {
        var response = JsonResponse(new
        {
            products = new[]
            {
                new
                {
                    product_name = "Yogur natural",
                    brands = "Danone",
                    nutriments = new Dictionary<string, object>
                    {
                        ["energy-kcal_100g"] = 63.5
                    }
                }
            }
        });

        var service = CreateService(response);
        var results = await service.BuscarAlimentosAsync("yogur");

        Assert.Single(results);
        Assert.Equal(63, results[0].KcalPor100g); // truncated to int
    }

    [Fact]
    public async Task BuscarAlimentos_Returns_Empty_On_ServerError()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var client = new HttpClient(handler.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        var service = new OpenFoodFactsService(factory.Object, _loggerMock.Object);
        var results = await service.BuscarAlimentosAsync("test");

        Assert.Empty(results);
    }
}

// ══════════════════════════════════════════════════════
//  DietasController — Manual diet creation & food CRUD
// ══════════════════════════════════════════════════════

public class DietasControllerManualTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly DietasController _controller;

    public DietasControllerManualTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        // Seed a user
        _db.Usuarios.Add(new Usuario
        {
            Id = 1,
            Username = "testuser",
            Nombre = "Test",
            Email = "test@test.com",
            PasswordHash = "hash",
            IsActive = true,
            FechaCreacion = DateTime.UtcNow
        });
        _db.SaveChanges();

        var pdfMock = new Mock<IPdfImportService>();
        var imgMock = new Mock<IImageImportService>();
        _controller = new DietasController(_db, pdfMock.Object, imgMock.Object);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CrearManual_CreatesFullHierarchy()
    {
        var dto = new CrearDietaManualDto("Mi Dieta", "Desc", new List<CrearDietaDiaDto>
        {
            new(DayOfWeek.Monday, null, new List<CrearComidaDto>
            {
                new(TipoComida.Desayuno, 0, null, new List<CrearAlimentoDto>
                {
                    new("Tostada integral", "2 rebanadas", "Cereales", 180),
                    new("Aceite de oliva", "10ml", "Grasas", 90)
                }),
                new(TipoComida.Almuerzo, 1, null, new List<CrearAlimentoDto>
                {
                    new("Pechuga de pollo", "200g", "Proteínas", 220)
                })
            }),
            new(DayOfWeek.Tuesday, "Día ligero", new List<CrearComidaDto>
            {
                new(TipoComida.Cena, 0, null, new List<CrearAlimentoDto>
                {
                    new("Ensalada mixta", "150g", null, 45)
                })
            })
        });

        var result = await _controller.CrearManual(1, dto);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var resumen = Assert.IsType<DietaResumenDto>(created.Value);
        Assert.Equal("Mi Dieta", resumen.Nombre);
        Assert.Equal("Desc", resumen.Descripcion);
        Assert.Null(resumen.ArchivoOriginal);

        // Verify full hierarchy in DB
        var dieta = await _db.Dietas
            .Include(d => d.Dias).ThenInclude(dd => dd.Comidas).ThenInclude(c => c.Alimentos)
            .FirstAsync(d => d.Id == resumen.Id);

        Assert.Equal(2, dieta.Dias.Count);

        var monday = dieta.Dias.First(d => d.DiaSemana == DayOfWeek.Monday);
        Assert.Equal(2, monday.Comidas.Count);
        var desayuno = monday.Comidas.First(c => c.Tipo == TipoComida.Desayuno);
        Assert.Equal(2, desayuno.Alimentos.Count);
        Assert.Equal("Tostada integral", desayuno.Alimentos[0].Nombre);
        Assert.Equal(180, desayuno.Alimentos[0].Kcal);
        Assert.Equal("Aceite de oliva", desayuno.Alimentos[1].Nombre);
        Assert.Equal(90, desayuno.Alimentos[1].Kcal);

        var tuesday = dieta.Dias.First(d => d.DiaSemana == DayOfWeek.Tuesday);
        Assert.Equal("Día ligero", tuesday.Nota);
        Assert.Single(tuesday.Comidas);
        Assert.Equal(45, tuesday.Comidas[0].Alimentos[0].Kcal);
    }

    [Fact]
    public async Task CrearManual_ReturnsNotFound_WhenUserMissing()
    {
        var dto = new CrearDietaManualDto("Test", null, new List<CrearDietaDiaDto>());
        var result = await _controller.CrearManual(999, dto);
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task CrearManual_NullableKcal_Preserved()
    {
        var dto = new CrearDietaManualDto("Dieta sin kcal", null, new List<CrearDietaDiaDto>
        {
            new(DayOfWeek.Wednesday, null, new List<CrearComidaDto>
            {
                new(TipoComida.Merienda, 0, null, new List<CrearAlimentoDto>
                {
                    new("Fruta", "1 pieza", null, null) // no kcal
                })
            })
        });

        var result = await _controller.CrearManual(1, dto);
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var resumen = Assert.IsType<DietaResumenDto>(created.Value);

        var alimento = await _db.Alimentos.FirstAsync();
        Assert.Null(alimento.Kcal);
        Assert.Equal("Fruta", alimento.Nombre);
    }

    [Fact]
    public async Task AgregarAlimento_AddsToExistingComida()
    {
        // Create a diet with a meal first
        var dieta = new Dieta
        {
            UsuarioId = 1, Nombre = "Test", FechaImportacion = DateTime.UtcNow,
            Dias = new List<DietaDia>
            {
                new() { DiaSemana = DayOfWeek.Monday, Comidas = new List<Comida>
                {
                    new() { Tipo = TipoComida.Desayuno, Orden = 0, Alimentos = new List<Alimento>() }
                }}
            }
        };
        _db.Dietas.Add(dieta);
        await _db.SaveChangesAsync();

        var comidaId = dieta.Dias[0].Comidas[0].Id;
        var dto = new CrearAlimentoDto("Café con leche", "200ml", "Bebidas", 35);

        var result = await _controller.AgregarAlimento(comidaId, dto);

        var alimentoDto = Assert.IsType<AlimentoDto>(result.Value);
        Assert.Equal("Café con leche", alimentoDto.Nombre);
        Assert.Equal("200ml", alimentoDto.Cantidad);
        Assert.Equal(35, alimentoDto.Kcal);
        Assert.True(alimentoDto.Id > 0);

        // Verify in DB
        Assert.Equal(1, await _db.Alimentos.CountAsync());
    }

    [Fact]
    public async Task AgregarAlimento_NotFound_WhenComidaMissing()
    {
        var dto = new CrearAlimentoDto("Test", null, null, null);
        var result = await _controller.AgregarAlimento(999, dto);
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task ActualizarAlimento_UpdatesAllFields()
    {
        _db.Alimentos.Add(new Alimento
        {
            ComidaId = 1,
            Nombre = "Viejo",
            Cantidad = "100g",
            Categoria = "Cereales",
            Kcal = 100,
            Comida = new Comida
            {
                Tipo = TipoComida.Desayuno, Orden = 0,
                DietaDia = new DietaDia
                {
                    DiaSemana = DayOfWeek.Monday,
                    Dieta = new Dieta { UsuarioId = 1, Nombre = "D", FechaImportacion = DateTime.UtcNow }
                }
            }
        });
        await _db.SaveChangesAsync();

        var alimento = await _db.Alimentos.FirstAsync();
        var dto = new CrearAlimentoDto("Nuevo nombre", "200g", "Proteínas", 250);

        var result = await _controller.ActualizarAlimento(alimento.Id, dto);
        Assert.IsType<NoContentResult>(result);

        var updated = await _db.Alimentos.FindAsync(alimento.Id);
        Assert.Equal("Nuevo nombre", updated!.Nombre);
        Assert.Equal("200g", updated.Cantidad);
        Assert.Equal("Proteínas", updated.Categoria);
        Assert.Equal(250, updated.Kcal);
    }

    [Fact]
    public async Task EliminarAlimento_RemovesFromDb()
    {
        _db.Alimentos.Add(new Alimento
        {
            ComidaId = 1,
            Nombre = "Borrar",
            Comida = new Comida
            {
                Tipo = TipoComida.Cena, Orden = 0,
                DietaDia = new DietaDia
                {
                    DiaSemana = DayOfWeek.Friday,
                    Dieta = new Dieta { UsuarioId = 1, Nombre = "D", FechaImportacion = DateTime.UtcNow }
                }
            }
        });
        await _db.SaveChangesAsync();

        var id = (await _db.Alimentos.FirstAsync()).Id;
        var result = await _controller.EliminarAlimento(id);
        Assert.IsType<NoContentResult>(result);
        Assert.Equal(0, await _db.Alimentos.CountAsync());
    }

    [Fact]
    public async Task EliminarAlimento_NotFound()
    {
        var result = await _controller.EliminarAlimento(999);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ObtenerDetalle_IncludesKcal()
    {
        var dieta = new Dieta
        {
            UsuarioId = 1, Nombre = "Detalle", FechaImportacion = DateTime.UtcNow,
            Dias = new List<DietaDia>
            {
                new() { DiaSemana = DayOfWeek.Monday, Comidas = new List<Comida>
                {
                    new() { Tipo = TipoComida.Desayuno, Orden = 0, Alimentos = new List<Alimento>
                    {
                        new() { Nombre = "Pan", Cantidad = "50g", Kcal = 130 },
                        new() { Nombre = "Mermelada", Cantidad = "20g", Kcal = null }
                    }}
                }}
            }
        };
        _db.Dietas.Add(dieta);
        await _db.SaveChangesAsync();

        var result = await _controller.ObtenerDetalle(dieta.Id);
        var detalle = Assert.IsType<DietaDetalleDto>(result.Value);

        var alimentos = detalle.Dias[0].Comidas[0].Alimentos;
        Assert.Equal(2, alimentos.Count);
        Assert.Equal(130, alimentos[0].Kcal);
        Assert.Null(alimentos[1].Kcal);
    }
}

// ══════════════════════════════════════════════════════
//  AlimentosController — search endpoint
// ══════════════════════════════════════════════════════

public class AlimentosControllerTests
{
    [Fact]
    public async Task Buscar_ReturnsBadRequest_WhenQueryTooShort()
    {
        var mockService = new Mock<IOpenFoodFactsService>();
        var controller = new AlimentosController(mockService.Object);

        var result = await controller.Buscar("a");
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Buscar_ReturnsBadRequest_WhenQueryEmpty()
    {
        var mockService = new Mock<IOpenFoodFactsService>();
        var controller = new AlimentosController(mockService.Object);

        var result = await controller.Buscar("");
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Buscar_CallsServiceAndReturnsResults()
    {
        var expected = new List<AlimentoBuscadoDto>
        {
            new("Pollo asado", "Hacendado", 167),
            new("Pechuga de pollo", null, 110)
        };
        var mockService = new Mock<IOpenFoodFactsService>();
        mockService.Setup(s => s.BuscarAlimentosAsync("pollo")).ReturnsAsync(expected);

        var controller = new AlimentosController(mockService.Object);
        var result = await controller.Buscar("pollo");

        var list = Assert.IsType<List<AlimentoBuscadoDto>>(result.Value);
        Assert.Equal(2, list.Count);
        Assert.Equal("Pollo asado", list[0].Nombre);
        Assert.Equal(167, list[0].KcalPor100g);
    }

    [Fact]
    public async Task Buscar_ReturnsEmptyList_WhenNoResults()
    {
        var mockService = new Mock<IOpenFoodFactsService>();
        mockService.Setup(s => s.BuscarAlimentosAsync("xyznoexiste")).ReturnsAsync(new List<AlimentoBuscadoDto>());

        var controller = new AlimentosController(mockService.Object);
        var result = await controller.Buscar("xyznoexiste");

        var list = Assert.IsType<List<AlimentoBuscadoDto>>(result.Value);
        Assert.Empty(list);
    }
}
