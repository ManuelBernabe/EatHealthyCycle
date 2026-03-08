using EatHealthyCycle.Models;

namespace EatHealthyCycle.Services.Interfaces;

public interface IPlanSemanalService
{
    Task<PlanSemanal> GenerarPlanAsync(int usuarioId, int dietaId, DateTime fechaInicio);
}
