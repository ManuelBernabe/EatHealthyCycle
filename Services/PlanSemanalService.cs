using Microsoft.EntityFrameworkCore;
using EatHealthyCycle.Data;
using EatHealthyCycle.Models;
using EatHealthyCycle.Services.Interfaces;

namespace EatHealthyCycle.Services;

public class PlanSemanalService : IPlanSemanalService
{
    private readonly AppDbContext _db;

    public PlanSemanalService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<PlanSemanal> GenerarPlanAsync(int usuarioId, int dietaId, DateTime fechaInicio)
    {
        // Ajustar al lunes más cercano
        while (fechaInicio.DayOfWeek != DayOfWeek.Monday)
            fechaInicio = fechaInicio.AddDays(-1);

        var dieta = await _db.Dietas
            .Include(d => d.Dias)
                .ThenInclude(dd => dd.Comidas)
                    .ThenInclude(c => c.Alimentos)
            .FirstOrDefaultAsync(d => d.Id == dietaId && d.UsuarioId == usuarioId)
            ?? throw new InvalidOperationException("Dieta no encontrada");

        var plan = new PlanSemanal
        {
            UsuarioId = usuarioId,
            DietaId = dietaId,
            FechaInicio = fechaInicio,
            FechaFin = fechaInicio.AddDays(6)
        };

        // Crear 7 días (lunes a domingo)
        for (int i = 0; i < 7; i++)
        {
            var fecha = fechaInicio.AddDays(i);
            var diaSemana = fecha.DayOfWeek;

            var planDia = new PlanDia
            {
                Fecha = fecha,
                DiaSemana = diaSemana
            };

            // Buscar el día correspondiente en la dieta
            var dietaDia = dieta.Dias.FirstOrDefault(d => d.DiaSemana == diaSemana);

            if (dietaDia != null)
            {
                foreach (var comida in dietaDia.Comidas.OrderBy(c => c.Orden))
                {
                    var descripcion = string.Join(", ",
                        comida.Alimentos.Select(a =>
                            a.Cantidad != null ? $"{a.Nombre} ({a.Cantidad})" : a.Nombre));

                    planDia.Comidas.Add(new PlanComida
                    {
                        ComidaId = comida.Id,
                        Tipo = comida.Tipo,
                        Descripcion = descripcion
                    });
                }
            }

            // Si no hay datos para este día, crear comidas vacías
            if (planDia.Comidas.Count == 0)
            {
                foreach (var tipo in Enum.GetValues<TipoComida>())
                {
                    planDia.Comidas.Add(new PlanComida
                    {
                        Tipo = tipo,
                        Descripcion = "(Sin asignar)"
                    });
                }
            }

            plan.Dias.Add(planDia);
        }

        _db.PlanesSemanal.Add(plan);
        await _db.SaveChangesAsync();

        return plan;
    }
}
