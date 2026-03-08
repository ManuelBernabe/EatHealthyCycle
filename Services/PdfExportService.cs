using EatHealthyCycle.Models;
using EatHealthyCycle.Services.Interfaces;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EatHealthyCycle.Services;

public class PdfExportService : IPdfExportService
{
    private static readonly Dictionary<DayOfWeek, string> NombresDia = new()
    {
        [DayOfWeek.Monday] = "Lunes",
        [DayOfWeek.Tuesday] = "Martes",
        [DayOfWeek.Wednesday] = "Miércoles",
        [DayOfWeek.Thursday] = "Jueves",
        [DayOfWeek.Friday] = "Viernes",
        [DayOfWeek.Saturday] = "Sábado",
        [DayOfWeek.Sunday] = "Domingo"
    };

    private static readonly Dictionary<TipoComida, string> NombresComida = new()
    {
        [TipoComida.PreDesayuno] = "Pre Desayuno",
        [TipoComida.Desayuno] = "Desayuno",
        [TipoComida.MediaManana] = "Media Mañana",
        [TipoComida.Almuerzo] = "Almuerzo",
        [TipoComida.Comida] = "Comida",
        [TipoComida.Merienda] = "Merienda",
        [TipoComida.Cena] = "Cena"
    };

    public byte[] GenerarPlanSemanalPdf(PlanSemanal plan)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(1, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Column(col =>
                {
                    col.Item().Text($"Plan Semanal: {plan.Dieta?.Nombre ?? "Mi Dieta"}")
                        .FontSize(16).Bold().FontColor(Colors.Green.Darken2);
                    col.Item().Text($"{plan.FechaInicio:dd/MM/yyyy} - {plan.FechaFin:dd/MM/yyyy}")
                        .FontSize(10).FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingBottom(10);
                });

                page.Content().Table(table =>
                {
                    // Columnas: 1 para tipo comida + 7 para días
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(80);
                        for (int i = 0; i < 7; i++)
                            columns.RelativeColumn();
                    });

                    // Cabecera con días
                    table.Header(header =>
                    {
                        header.Cell().Background(Colors.Green.Darken2)
                            .Padding(4).Text("").FontColor(Colors.White);

                        var diasOrdenados = plan.Dias
                            .OrderBy(d => d.DiaSemana == DayOfWeek.Sunday ? 7 : (int)d.DiaSemana)
                            .ToList();

                        foreach (var dia in diasOrdenados)
                        {
                            var nombreDia = NombresDia.GetValueOrDefault(dia.DiaSemana, dia.DiaSemana.ToString());
                            header.Cell().Background(Colors.Green.Darken2)
                                .Padding(4).AlignCenter()
                                .Text($"{nombreDia}\n{dia.Fecha:dd/MM}")
                                .FontColor(Colors.White).Bold();
                        }
                    });

                    // Filas por tipo de comida
                    foreach (var tipo in Enum.GetValues<TipoComida>())
                    {
                        var nombreTipo = NombresComida.GetValueOrDefault(tipo, tipo.ToString());
                        var bgColor = tipo switch
                        {
                            TipoComida.PreDesayuno => Colors.Amber.Lighten4,
                            TipoComida.Desayuno => Colors.Orange.Lighten4,
                            TipoComida.MediaManana => Colors.Yellow.Lighten4,
                            TipoComida.Almuerzo => Colors.Green.Lighten4,
                            TipoComida.Comida => Colors.Teal.Lighten4,
                            TipoComida.Merienda => Colors.Blue.Lighten4,
                            TipoComida.Cena => Colors.Purple.Lighten4,
                            _ => Colors.Grey.Lighten4
                        };

                        table.Cell().Background(bgColor).Padding(4)
                            .Text(nombreTipo).Bold().FontSize(8);

                        var diasOrdenados = plan.Dias
                            .OrderBy(d => d.DiaSemana == DayOfWeek.Sunday ? 7 : (int)d.DiaSemana)
                            .ToList();

                        foreach (var dia in diasOrdenados)
                        {
                            var comidas = dia.Comidas.Where(c => c.Tipo == tipo).ToList();
                            table.Cell().Background(bgColor).Padding(3)
                                .Text(text =>
                                {
                                    if (comidas.Any())
                                    {
                                        foreach (var comida in comidas)
                                        {
                                            text.Line(comida.Descripcion).FontSize(8);
                                        }
                                    }
                                    else
                                    {
                                        text.Span("-").FontSize(8).FontColor(Colors.Grey.Medium);
                                    }
                                });
                        }
                    }
                });

                page.Footer().AlignCenter()
                    .Text("Generado por EatHealthyCycle").FontSize(7).FontColor(Colors.Grey.Medium);
            });
        });

        return document.GeneratePdf();
    }
}
