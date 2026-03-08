using EatHealthyCycle.Models;

namespace EatHealthyCycle.Services.Interfaces;

public interface IPdfExportService
{
    byte[] GenerarPlanSemanalPdf(PlanSemanal plan);
}
