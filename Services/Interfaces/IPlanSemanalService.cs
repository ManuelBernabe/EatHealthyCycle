using EatHealthyCycle.Models;

namespace EatHealthyCycle.Services.Interfaces;

public interface IPlanSemanalService
{
    Task<PlanSemanal> GenerarPlanAsync(int usuarioId, int dietaId, DateTime fechaInicio);

    /// <summary>
    /// Recompute Descripcion of every PlanComida linked to a Comida from its current
    /// Alimentos. Skips manually-added rows (ComidaId is null). Returns the number of
    /// rows updated.
    /// </summary>
    Task<int> RefrescarDescripcionAsync(int planId);
}
