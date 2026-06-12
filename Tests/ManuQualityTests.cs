using EatHealthyCycle.Data;
using EatHealthyCycle.Models;
using EatHealthyCycle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EatHealthyCycle.Tests;

/// <summary>
/// Quality regression tests for the "DIETA MANU 220526.pdf" parse.
/// These lock in the fixes for the three problems reported in production:
///   1. Lost quantities — a food whose grams wrapped onto its own visual line
///      ("CHAMPIÑON" / "200G") used to drop the quantity entirely.
///   2. Fragmented / truncated names — "HUEVO DE GALLINA TALLA M 4 UNIDADES"
///      used to split into "HUEVO DE" + "GALLINA TALLA M" and even lose "TALLA M 4"
///      because "TALLA" is an anthropometric-table header word.
///   3. Cross-section contamination — the plain-text "-Cena: LIBRE" of the día off
///      leaked into Thursday's DESAYUNO cell.
/// </summary>
public class ManuQualityTests
{
    private const string PdfPath = @"C:\Users\sonim\Downloads\DIETA MANU 220526.pdf";

    private static async Task<Dieta> ParseAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("ManuQuality_" + Guid.NewGuid()).Options;
        var db = new AppDbContext(options);
        db.Usuarios.Add(new Usuario { Id = 2, Username = "m", Nombre = "M", Email = "m@t.l", PasswordHash = "x", IsActive = true, FechaCreacion = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var pdfService = new PdfImportService(db, NullLogger<PdfImportService>.Instance);
        await using var fs = File.OpenRead(PdfPath);
        return await pdfService.ImportarDietaDesdePdfAsync(2, "Dieta Manu", fs, "manu.pdf");
    }

    private static Comida Meal(Dieta d, DayOfWeek day, TipoComida tipo) =>
        d.Dias.First(x => x.DiaSemana == day).Comidas.First(c => c.Tipo == tipo);

    private static string Joined(Comida c) =>
        string.Join(" | ", c.Alimentos.Select(a => $"{a.Nombre}={a.Cantidad}".ToUpperInvariant()));

    [SkippableFact]
    public async Task Quantities_OnWrappedLines_AreNotLost()
    {
        Skip.IfNot(File.Exists(PdfPath), "PDF de regresión no presente");
        var dieta = await ParseAsync();

        // Monday CENA: every item's grams wrap to a second visual line in the source.
        var cena = Meal(dieta, DayOfWeek.Monday, TipoComida.Cena);
        Alimento Item(string startsWith) => cena.Alimentos.First(a =>
            a.Nombre.StartsWith(startsWith, StringComparison.OrdinalIgnoreCase));

        Assert.Equal("200G", Item("CHAMPIÑON").Cantidad, ignoreCase: true);
        Assert.Equal("100G", Item("AGUACATE").Cantidad, ignoreCase: true);
        Assert.Equal("1G", Item("SAL").Cantidad, ignoreCase: true);
    }

    [SkippableFact]
    public async Task EggItem_KeepsTallaAndUnits_NotFragmented()
    {
        Skip.IfNot(File.Exists(PdfPath), "PDF de regresión no presente");
        var dieta = await ParseAsync();

        // The egg item wraps across THREE visual lines and contains "TALLA" (a header word).
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                    DayOfWeek.Thursday, DayOfWeek.Friday })
        {
            var desayuno = Meal(dieta, day, TipoComida.Desayuno);
            var huevo = desayuno.Alimentos.FirstOrDefault(a =>
                a.Nombre.StartsWith("HUEVO", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(huevo);
            var full = $"{huevo!.Nombre} {huevo.Cantidad}".ToUpperInvariant();
            Assert.Contains("GALLINA", full);
            Assert.Contains("TALLA M", full);     // must not be dropped as a header word
            Assert.Contains("4 UNIDADES", full);  // unit tail must merge back, not orphan

            // No orphan fragment left behind
            Assert.DoesNotContain(desayuno.Alimentos, a =>
                a.Nombre.Trim().Equals("HUEVO DE", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(desayuno.Alimentos, a =>
                a.Nombre.Trim().Equals("UNIDADES", StringComparison.OrdinalIgnoreCase));
        }
    }

    [SkippableFact]
    public async Task UnitTailFragments_AreMergedBack()
    {
        Skip.IfNot(File.Exists(PdfPath), "PDF de regresión no presente");
        var dieta = await ParseAsync();

        // "MANZANA 1" + "UNIDAD" → single item
        var meriendaSat = Meal(dieta, DayOfWeek.Saturday, TipoComida.Merienda);
        Assert.Contains(meriendaSat.Alimentos, a =>
            a.Nombre.ToUpperInvariant().Contains("MANZANA"));
        Assert.DoesNotContain(meriendaSat.Alimentos, a =>
            a.Nombre.Trim().Equals("UNIDAD", StringComparison.OrdinalIgnoreCase));

        // "PIÑA ENLATADA" + "EN SU JUGO 2" + "RODAJAS" → single item
        var meriendaTue = Meal(dieta, DayOfWeek.Tuesday, TipoComida.Merienda);
        var pina = meriendaTue.Alimentos.First(a => a.Nombre.ToUpperInvariant().StartsWith("PIÑA"));
        Assert.Contains("JUGO", pina.Nombre.ToUpperInvariant());
        Assert.Contains("RODAJAS", pina.Nombre.ToUpperInvariant());
        Assert.DoesNotContain(meriendaTue.Alimentos, a =>
            a.Nombre.Trim().Equals("RODAJAS", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task DiaOff_PlainText_DoesNotLeakIntoTableCells()
    {
        Skip.IfNot(File.Exists(PdfPath), "PDF de regresión no presente");
        var dieta = await ParseAsync();

        var jueDesayuno = Meal(dieta, DayOfWeek.Thursday, TipoComida.Desayuno);
        var text = Joined(jueDesayuno);
        Assert.DoesNotContain("CENA", text);
        Assert.DoesNotContain("LIBRE", text);

        // The día off "jamón serrano 60g" wraps; it must be one item, not "jamón" + "serrano".
        var domAlmuerzo = Meal(dieta, DayOfWeek.Sunday, TipoComida.Almuerzo);
        Assert.Contains(domAlmuerzo.Alimentos, a =>
            a.Nombre.ToLowerInvariant().Contains("jamón") &&
            a.Nombre.ToLowerInvariant().Contains("serrano"));
    }

    [SkippableFact]
    public async Task NoEmptyQuantitiesForItemsThatHaveThemInSource()
    {
        Skip.IfNot(File.Exists(PdfPath), "PDF de regresión no presente");
        var dieta = await ParseAsync();

        // Sanity: across the whole diet, no item should be a bare orphaned unit/quantity.
        foreach (var dia in dieta.Dias)
        foreach (var c in dia.Comidas)
        foreach (var a in c.Alimentos)
        {
            var n = a.Nombre.Trim().ToUpperInvariant();
            Assert.False(n is "UNIDAD" or "UNIDADES" or "RODAJAS" or "RODAJA" or "G" or "GR" or "ML",
                $"{dia.DiaSemana}/{c.Tipo}: fragmento de unidad huérfano '{a.Nombre}'");
        }
    }
}
