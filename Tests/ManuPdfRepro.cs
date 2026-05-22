using EatHealthyCycle.Data;
using EatHealthyCycle.Models;
using EatHealthyCycle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EatHealthyCycle.Tests;

/// <summary>
/// Regression tests against the "DIETA MANU 220526.pdf" produced by RGANUTRI:
///   - Page 5 col 1 header is "DIA DE PIERNA" (no digit) — column 1 must map to Monday.
///   - Page 7 col 3 header is "SABADO DIA OFF" — column 3 must map to Saturday.
///   - The last detected DIA column must NOT extend to the page right edge,
///     otherwise it absorbs content from the un-numbered Saturday column.
/// </summary>
public class ManuPdfReproTests
{
    private const string PdfPath = @"C:\Users\sonim\Downloads\DIETA MANU 220526.pdf";

    [SkippableFact]
    public async Task ImportarDietaManu_TodosLosDiasSinContaminacion()
    {
        Skip.IfNot(File.Exists(PdfPath), $"PDF de regresión no presente: {PdfPath}");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("ManuRepro_" + Guid.NewGuid())
            .Options;
        using var db = new AppDbContext(options);

        db.Usuarios.Add(new Usuario
        {
            Id = 2,
            Username = "manu",
            Nombre = "Manu",
            Email = "manu@test.local",
            PasswordHash = "x",
            IsActive = true,
            FechaCreacion = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var pdfService = new PdfImportService(db, NullLogger<PdfImportService>.Instance);

        Dieta dieta;
        await using (var fs = File.OpenRead(PdfPath))
        {
            dieta = await pdfService.ImportarDietaDesdePdfAsync(2, "Dieta Manu", fs, "DIETA MANU 220526.pdf");
        }

        // El PDF cubre lunes-domingo (6 días de entreno + día off → sábado y domingo)
        var dias = dieta.Dias.Select(d => d.DiaSemana).ToHashSet();
        Assert.Contains(DayOfWeek.Monday, dias);     // "DIA DE PIERNA"
        Assert.Contains(DayOfWeek.Tuesday, dias);
        Assert.Contains(DayOfWeek.Wednesday, dias);
        Assert.Contains(DayOfWeek.Thursday, dias);
        Assert.Contains(DayOfWeek.Friday, dias);
        Assert.Contains(DayOfWeek.Saturday, dias);   // "SABADO DIA OFF"
        Assert.Contains(DayOfWeek.Sunday, dias);     // Día off plain text

        // Cada día debe tener al menos 3 comidas (desayuno, comida, cena)
        foreach (var d in dieta.Dias)
        {
            Assert.True(d.Comidas.Count >= 3,
                $"{d.DiaSemana} solo tiene {d.Comidas.Count} comidas — la columna se perdió o se fusionó");
        }

        // Detectar contaminación entre columnas: nombres con palabras duplicadas inmediatas
        // ("QUESO QUESO", "MANCHEGO MANCHEGO", etc.) aparecen sólo si dos columnas se mezclan.
        foreach (var d in dieta.Dias)
        foreach (var c in d.Comidas)
        foreach (var a in c.Alimentos)
        {
            var words = a.Nombre.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < words.Length; i++)
            {
                Assert.False(words[i].Equals(words[i - 1], StringComparison.OrdinalIgnoreCase),
                    $"Contaminación entre columnas en {d.DiaSemana}/{c.Tipo}: '{a.Nombre}'");
            }
        }

        // Plan generation también debe funcionar
        var planService = new PlanSemanalService(db);
        var plan = await planService.GenerarPlanAsync(2, dieta.Id, new DateTime(2026, 5, 18));
        Assert.Equal(7, plan.Dias.Count);
        // Ningún día debería quedar como "(Sin asignar)" puro (señal de día perdido por el parser)
        foreach (var pd in plan.Dias)
        {
            var todasSinAsignar = pd.Comidas.All(c => c.Descripcion == "(Sin asignar)");
            Assert.False(todasSinAsignar,
                $"{pd.DiaSemana} acabó completamente vacío — la dieta perdió ese día");
        }

        // Las celdas COMIDA / MERIENDA / CENA del lunes (DIA DE PIERNA) son la prueba canónica
        // del fix: la versión anterior metía proteína whey y agua mineral en COMIDA, y CHAMPIÑÓN
        // en MERIENDA. Verificamos que cada item esté en el slot correcto.
        var lunes = dieta.Dias.First(d => d.DiaSemana == DayOfWeek.Monday);
        string ComidaAsText(TipoComida t) => string.Join(" | ",
            lunes.Comidas.FirstOrDefault(c => c.Tipo == t)?.Alimentos
                .Select(a => a.Nombre.ToUpperInvariant()) ?? Enumerable.Empty<string>());

        var comidaText = ComidaAsText(TipoComida.Comida);
        Assert.DoesNotContain("PROTE", comidaText);   // proteína whey pertenece a MERIENDA
        Assert.DoesNotContain("AGUA MINERAL", comidaText);
        Assert.DoesNotContain("NUEZ", comidaText);

        var meriendaText = ComidaAsText(TipoComida.Merienda);
        Assert.DoesNotContain("CHAMPI", meriendaText); // champiñón pertenece a CENA
        Assert.DoesNotContain("BERBERECHO", meriendaText);
        Assert.DoesNotContain("PECHUGA", meriendaText); // pechuga de pollo de la cena

        var cenaText = ComidaAsText(TipoComida.Cena);
        Assert.Contains("BERBERECHO", cenaText);
        Assert.Contains("CHAMPI", cenaText);
    }

}
