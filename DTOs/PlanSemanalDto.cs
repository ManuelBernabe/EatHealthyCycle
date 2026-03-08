using EatHealthyCycle.Models;

namespace EatHealthyCycle.DTOs;

public record CrearPlanSemanalDto(int DietaId, DateTime FechaInicio);
public record CrearPlanManualDto(string Nombre, DateTime FechaInicio);
public record AddPlanComidaDto(TipoComida Tipo, string Descripcion);

public record PlanSemanalResumenDto(int Id, int? DietaId, string DietaNombre, DateTime FechaInicio, DateTime FechaFin);

public record PlanSemanalDetalleDto(
    int Id,
    string DietaNombre,
    DateTime FechaInicio,
    DateTime FechaFin,
    List<PlanDiaDto> Dias);

public record PlanDiaDto(int Id, DateTime Fecha, DayOfWeek DiaSemana, List<PlanComidaDto> Comidas);

public record PlanComidaDto(int Id, TipoComida Tipo, string Descripcion, bool Completada, DateTime? FechaCompletada);

public record CumplimientoDto(int TotalComidas, int ComidasCompletadas, decimal PorcentajeCumplimiento);
