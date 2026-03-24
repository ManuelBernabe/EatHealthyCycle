using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EatHealthyCycle.DTOs;
using EatHealthyCycle.Services.Interfaces;

namespace EatHealthyCycle.Controllers;

[ApiController]
[Route("api/alimentos")]
[Authorize]
public class AlimentosController : ControllerBase
{
    private readonly IOpenFoodFactsService _offService;

    public AlimentosController(IOpenFoodFactsService offService)
    {
        _offService = offService;
    }

    [HttpGet("buscar")]
    public async Task<ActionResult<List<AlimentoBuscadoDto>>> Buscar([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return BadRequest("El término de búsqueda debe tener al menos 2 caracteres");

        var resultados = await _offService.BuscarAlimentosAsync(q);
        return resultados;
    }
}
