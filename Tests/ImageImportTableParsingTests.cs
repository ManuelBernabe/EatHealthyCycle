using EatHealthyCycle.Models;
using EatHealthyCycle.Services;
using Microsoft.Extensions.Logging;
using Moq;
using static EatHealthyCycle.Services.ImageImportService;

namespace EatHealthyCycle.Tests;

/// <summary>
/// Tests that simulate OCR output from a real diet table image with:
/// - 7 day columns: "Dia 1" through "Dia 7"
/// - 5 meal rows: Desayuno, Tentempié 1, Comida, Merienda 1, Cena
/// - Spanish food items with quantities like "Bebida de soja con calcio: 300g (1 taza)"
/// </summary>
public class ImageImportTableParsingTests
{
    private readonly ImageImportService _service;

    public ImageImportTableParsingTests()
    {
        var loggerMock = new Mock<ILogger<ImageImportService>>();
        _service = new ImageImportService(null!, loggerMock.Object);
    }

    // ─── Helper to build OcrWord ─────────────────────────────────────────
    private static OcrWord W(string text, int left, int top, int width = 80, int height = 20, int conf = 90)
        => new()
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Right = left + width,
            Bottom = top + height,
            CenterX = left + width / 2.0,
            CenterY = top + height / 2.0,
            Confidence = conf,
            BlockNum = 1,
            LineNum = 1,
            ParNum = 1
        };

    // ─── DetectDayColumns tests ──────────────────────────────────────────

    [Fact]
    public void DetectDayColumns_FindsDia1Through7()
    {
        // Simulate the image headers: "Dia 1", "Dia 2", ..., "Dia 7"
        var words = new List<OcrWord>
        {
            W("Dia", 120, 30, 40), W("1", 165, 30, 15),
            W("Dia", 260, 30, 40), W("2", 305, 30, 15),
            W("Dia", 400, 30, 40), W("3", 445, 30, 15),
            W("Dia", 540, 30, 40), W("4", 585, 30, 15),
            W("Dia", 680, 30, 40), W("5", 725, 30, 15),
            W("Dia", 820, 30, 40), W("6", 865, 30, 15),
            W("Dia", 960, 30, 40), W("7", 1005, 30, 15),
        };

        var cols = _service.DetectDayColumns(words);

        Assert.Equal(7, cols.Count);
        Assert.Equal(DayOfWeek.Monday, cols[0].DayOfWeek);
        Assert.Equal(DayOfWeek.Tuesday, cols[1].DayOfWeek);
        Assert.Equal(DayOfWeek.Wednesday, cols[2].DayOfWeek);
        Assert.Equal(DayOfWeek.Thursday, cols[3].DayOfWeek);
        Assert.Equal(DayOfWeek.Friday, cols[4].DayOfWeek);
        Assert.Equal(DayOfWeek.Saturday, cols[5].DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, cols[6].DayOfWeek);

        // Columns should be ordered left-to-right
        for (int i = 1; i < cols.Count; i++)
            Assert.True(cols[i].ContentLeft > cols[i - 1].ContentLeft);
    }

    [Fact]
    public void DetectDayColumns_FindsDayNames()
    {
        // Some images use Lunes, Martes, etc.
        var words = new List<OcrWord>
        {
            W("Lunes", 120, 30, 60),
            W("Martes", 260, 30, 60),
            W("Miércoles", 400, 30, 90),
        };

        var cols = _service.DetectDayColumns(words);

        Assert.Equal(3, cols.Count);
        Assert.Equal(DayOfWeek.Monday, cols[0].DayOfWeek);
        Assert.Equal(DayOfWeek.Tuesday, cols[1].DayOfWeek);
        Assert.Equal(DayOfWeek.Wednesday, cols[2].DayOfWeek);
    }

    [Fact]
    public void DetectDayColumns_NoDuplicates()
    {
        // Image might have "Dia" and "1" recognized twice
        var words = new List<OcrWord>
        {
            W("Dia", 120, 30, 40), W("1", 165, 30, 15),
            W("Dia", 120, 32, 40), W("1", 165, 32, 15), // duplicate
            W("Dia", 260, 30, 40), W("2", 305, 30, 15),
        };

        var cols = _service.DetectDayColumns(words);
        Assert.Equal(2, cols.Count);
    }

    // ─── DetectMealRows tests ────────────────────────────────────────────

    [Fact]
    public void DetectMealRows_FindsAllMealTypes()
    {
        var dayColumns = new List<OcrWord>
        {
            W("Dia", 120, 30, 40), W("1", 165, 30, 15),
        };
        var dayCols = _service.DetectDayColumns(dayColumns);

        // Meal labels from the real image
        var words = new List<OcrWord>(dayColumns)
        {
            W("Desayuno", 20, 80, 90),
            W("Tentempié", 20, 250, 100), W("1", 125, 250, 15),
            W("Comida", 20, 420, 70),
            W("Merienda", 20, 650, 90), W("1", 115, 650, 15),
            W("Cena", 20, 850, 50),
        };

        var rows = _service.DetectMealRows(words, dayCols);

        Assert.Equal(5, rows.Count);
        Assert.Equal(TipoComida.Desayuno, rows[0].MealType);
        Assert.Equal(TipoComida.Almuerzo, rows[1].MealType); // Tentempié maps to Almuerzo
        Assert.Equal(TipoComida.Comida, rows[2].MealType);
        Assert.Equal(TipoComida.Merienda, rows[3].MealType);
        Assert.Equal(TipoComida.Cena, rows[4].MealType);

        // Rows should be ordered top-to-bottom
        for (int i = 1; i < rows.Count; i++)
            Assert.True(rows[i].Top > rows[i - 1].Top);
    }

    [Fact]
    public void DetectMealRows_HandlesTentempie()
    {
        // "Tentempié" should map to Almuerzo
        var dayColumns = new List<OcrWord> { W("Dia", 120, 30, 40), W("1", 165, 30, 15) };
        var dayCols = _service.DetectDayColumns(dayColumns);

        var words = new List<OcrWord>(dayColumns)
        {
            W("Tentempié", 20, 100, 100),
        };

        var rows = _service.DetectMealRows(words, dayCols);
        Assert.Single(rows);
        Assert.Equal(TipoComida.Almuerzo, rows[0].MealType);
    }

    // ─── ParseFoodLine tests ─────────────────────────────────────────────

    [Fact]
    public void ParseFoodLine_ColonFormat_BebidaDeSoja()
    {
        var result = ImageImportService.ParseFoodLine("Bebida de soja con calcio: 300g (1 taza)");
        Assert.NotNull(result);
        Assert.Equal("BEBIDA DE SOJA CON CALCIO", result.Name);
        Assert.Equal("300g (1 taza)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_ProteinaDeSuero()
    {
        var result = ImageImportService.ParseFoodLine("proteina de suero 90%: 40g");
        Assert.NotNull(result);
        Assert.Equal("PROTEINA DE SUERO 90%", result.Name);
        Assert.Equal("40g", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_Canela()
    {
        var result = ImageImportService.ParseFoodLine("Canela: 3g (Al gusto)");
        Assert.NotNull(result);
        Assert.Equal("CANELA", result.Name);
        Assert.Equal("3g (Al gusto)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_NuezPelada()
    {
        var result = ImageImportService.ParseFoodLine("Nuez pelada: 15g (3 nueces)");
        Assert.NotNull(result);
        Assert.Equal("NUEZ PELADA", result.Name);
        Assert.Equal("15g (3 nueces)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_AguacatePalta()
    {
        var result = ImageImportService.ParseFoodLine("Aguacate/ palta: 200g (1 pieza)");
        Assert.NotNull(result);
        Assert.Equal("AGUACATE/ PALTA", result.Name);
        Assert.Equal("200g (1 pieza)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_HuevoDeGallina()
    {
        var result = ImageImportService.ParseFoodLine("Huevo de gallina: 195g (3 unidades talla M)");
        Assert.NotNull(result);
        Assert.Equal("HUEVO DE GALLINA", result.Name);
        Assert.Equal("195g (3 unidades talla M)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_SalYodada()
    {
        var result = ImageImportService.ParseFoodLine("Sal yodada: 0g");
        Assert.NotNull(result);
        Assert.Equal("SAL YODADA", result.Name);
        Assert.Equal("0g", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_Bacon()
    {
        var result = ImageImportService.ParseFoodLine("Bacon: 40g (2 lonchas)");
        Assert.NotNull(result);
        Assert.Equal("BACON", result.Name);
        Assert.Equal("40g (2 lonchas)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_QuesoManchego()
    {
        var result = ImageImportService.ParseFoodLine("Queso manchego curado: 30g (2 cortadas finas)");
        Assert.NotNull(result);
        Assert.Equal("QUESO MANCHEGO CURADO", result.Name);
        Assert.Equal("30g (2 cortadas finas)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_ChampignonOSeta()
    {
        var result = ImageImportService.ParseFoodLine("Champiñón o seta: 200g- (10 unidades medianas)");
        Assert.NotNull(result);
        Assert.Equal("CHAMPIÑÓN O SETA", result.Name);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_AceiteDeOliva()
    {
        var result = ImageImportService.ParseFoodLine("Aceite de oliva: 10g (1 cucharada sopera)");
        Assert.NotNull(result);
        Assert.Equal("ACEITE DE OLIVA", result.Name);
        Assert.Equal("10g (1 cucharada sopera)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_FleteDeTernera()
    {
        var result = ImageImportService.ParseFoodLine("Filete de ternera: 160g");
        Assert.NotNull(result);
        Assert.Equal("FILETE DE TERNERA", result.Name);
        Assert.Equal("160g", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_AtunEnlatado()
    {
        var result = ImageImportService.ParseFoodLine("Atún enlatado en agua: 120g (2 latas pequeñas redondas)");
        Assert.NotNull(result);
        Assert.Equal("ATÚN ENLATADO EN AGUA", result.Name);
        Assert.Equal("120g (2 latas pequeñas redondas)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_PanIntegral()
    {
        var result = ImageImportService.ParseFoodLine("Pan integral de trigo: 30g- (1 rebanada (3 dedos de grosor))");
        Assert.NotNull(result);
        Assert.Equal("PAN INTEGRAL DE TRIGO", result.Name);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_ZanahoriaWithParens()
    {
        var result = ImageImportService.ParseFoodLine("Zanahoria: 80g (1 unidad mediana (80g))");
        Assert.NotNull(result);
        Assert.Equal("ZANAHORIA", result.Name);
        Assert.Equal("80g (1 unidad mediana (80g))", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_Pollo()
    {
        var result = ImageImportService.ParseFoodLine("Pollo, pechuga, solomillo: 160g");
        Assert.NotNull(result);
        Assert.Equal("POLLO, PECHUGA, SOLOMILLO", result.Name);
        Assert.Equal("160g", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_AguaMineral()
    {
        var result = ImageImportService.ParseFoodLine("Agua mineral: 300g");
        Assert.NotNull(result);
        Assert.Equal("AGUA MINERAL", result.Name);
        Assert.Equal("300g", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_CacaoEnPolvo()
    {
        var result = ImageImportService.ParseFoodLine("Cacao en polvo desgrasado sin azúcar: 10g (1 cucharada de postre)");
        Assert.NotNull(result);
        Assert.Equal("CACAO EN POLVO DESGRASADO SIN AZÚCAR", result.Name);
        Assert.Equal("10g (1 cucharada de postre)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_AlmendraSinCascara()
    {
        var result = ImageImportService.ParseFoodLine("Almendra sin cáscara: 8g (5 almendras)");
        Assert.NotNull(result);
        Assert.Equal("ALMENDRA SIN CÁSCARA", result.Name);
        Assert.Equal("8g (5 almendras)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_Naranja()
    {
        var result = ImageImportService.ParseFoodLine("Naranja: 230g (1 unidad mediana)");
        Assert.NotNull(result);
        Assert.Equal("NARANJA", result.Name);
        Assert.Equal("230g (1 unidad mediana)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_PimientoRojo()
    {
        var result = ImageImportService.ParseFoodLine("Pimiento rojo: 60g (4 rodajas)");
        Assert.NotNull(result);
        Assert.Equal("PIMIENTO ROJO", result.Name);
        Assert.Equal("60g (4 rodajas)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_CalabacínHervido()
    {
        var result = ImageImportService.ParseFoodLine("Calabacín hervido: 200g (1 plato grande)");
        Assert.NotNull(result);
        Assert.Equal("CALABACÍN HERVIDO", result.Name);
        Assert.Equal("200g (1 plato grande)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_CebollaBlanca()
    {
        var result = ImageImportService.ParseFoodLine("Cebolla blanca: 60g (1/2 unidad pequeña)");
        Assert.NotNull(result);
        Assert.Equal("CEBOLLA BLANCA", result.Name);
        Assert.Equal("60g (1/2 unidad pequeña)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_LomoAdobado()
    {
        var result = ImageImportService.ParseFoodLine("Lomo adobado: 160g");
        Assert.NotNull(result);
        Assert.Equal("LOMO ADOBADO", result.Name);
        Assert.Equal("160g", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_PiñaEnlatada()
    {
        var result = ImageImportService.ParseFoodLine("Piña, enlatada en su jugo: 195g (3 rodajas)");
        Assert.NotNull(result);
        Assert.Equal("PIÑA, ENLATADA EN SU JUGO", result.Name);
        Assert.Equal("195g (3 rodajas)", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_ArrozIntegral()
    {
        var result = ImageImportService.ParseFoodLine("Arroz integral hervido: 130g");
        Assert.NotNull(result);
        Assert.Equal("ARROZ INTEGRAL HERVIDO", result.Name);
        Assert.Equal("130g", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_Lechuga()
    {
        var result = ImageImportService.ParseFoodLine("Lechuga: 60g (3 hojas grandes)");
        Assert.NotNull(result);
        Assert.Equal("LECHUGA", result.Name);
        Assert.Equal("60g (3 hojas grandes)", result.Quantity);
    }

    // ─── Full table parsing (end-to-end) ─────────────────────────────────

    [Fact]
    public void ParseTableFromWords_RealDietImage_Detects7Days()
    {
        var words = BuildRealDietImageWords();
        var dieta = _service.ParseTableFromWords(words, 1, "Test Diet", "diet.jpg");

        Assert.True(dieta.Dias.Count >= 5, $"Expected at least 5 days, got {dieta.Dias.Count}");
        Assert.True(dieta.Dias.Count <= 7, $"Expected at most 7 days, got {dieta.Dias.Count}");
    }

    [Fact]
    public void ParseTableFromWords_RealDietImage_DetectsDesayuno()
    {
        var words = BuildRealDietImageWords();
        var dieta = _service.ParseTableFromWords(words, 1, "Test Diet", "diet.jpg");

        // Each day should have a Desayuno meal
        foreach (var dia in dieta.Dias)
        {
            var desayuno = dia.Comidas.FirstOrDefault(c => c.Tipo == TipoComida.Desayuno);
            Assert.NotNull(desayuno);
            Assert.True(desayuno.Alimentos.Count > 0,
                $"Desayuno for {dia.DiaSemana} should have food items");
        }
    }

    [Fact]
    public void ParseTableFromWords_RealDietImage_DetectsComida()
    {
        var words = BuildRealDietImageWords();
        var dieta = _service.ParseTableFromWords(words, 1, "Test Diet", "diet.jpg");

        foreach (var dia in dieta.Dias)
        {
            var comida = dia.Comidas.FirstOrDefault(c => c.Tipo == TipoComida.Comida);
            Assert.NotNull(comida);
            Assert.True(comida.Alimentos.Count > 0,
                $"Comida for {dia.DiaSemana} should have food items");
        }
    }

    [Fact]
    public void ParseTableFromWords_RealDietImage_DetectsCena()
    {
        var words = BuildRealDietImageWords();
        var dieta = _service.ParseTableFromWords(words, 1, "Test Diet", "diet.jpg");

        foreach (var dia in dieta.Dias)
        {
            var cena = dia.Comidas.FirstOrDefault(c => c.Tipo == TipoComida.Cena);
            Assert.NotNull(cena);
            Assert.True(cena.Alimentos.Count > 0,
                $"Cena for {dia.DiaSemana} should have food items");
        }
    }

    [Fact]
    public void ParseTableFromWords_RealDietImage_HasBebidaDeSoja()
    {
        var words = BuildRealDietImageWords();
        var dieta = _service.ParseTableFromWords(words, 1, "Test Diet", "diet.jpg");

        // "Bebida de soja con calcio: 300g (1 taza)" appears in Desayuno for all days
        var allAlimentos = dieta.Dias
            .SelectMany(d => d.Comidas)
            .Where(c => c.Tipo == TipoComida.Desayuno)
            .SelectMany(c => c.Alimentos)
            .ToList();

        Assert.Contains(allAlimentos, a =>
            a.Nombre.Contains("BEBIDA", StringComparison.OrdinalIgnoreCase) ||
            a.Nombre.Contains("SOJA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseTableFromWords_RealDietImage_SetsUsuarioAndNombre()
    {
        var words = BuildRealDietImageWords();
        var dieta = _service.ParseTableFromWords(words, 42, "Mi Dieta Semanal", "foto.jpg");

        Assert.Equal(42, dieta.UsuarioId);
        Assert.Equal("Mi Dieta Semanal", dieta.Nombre);
        Assert.Equal("foto.jpg", dieta.ArchivoOriginal);
        Assert.Equal("Importada desde imagen", dieta.Descripcion);
    }

    [Fact]
    public void ParseTableFromWords_RealDietImage_MealOrderIsCorrect()
    {
        var words = BuildRealDietImageWords();
        var dieta = _service.ParseTableFromWords(words, 1, "Test Diet", "diet.jpg");

        foreach (var dia in dieta.Dias)
        {
            var tipos = dia.Comidas.Select(c => c.Tipo).ToList();

            // Desayuno should come before Comida, Comida before Cena
            if (tipos.Contains(TipoComida.Desayuno) && tipos.Contains(TipoComida.Comida))
            {
                var desIdx = tipos.IndexOf(TipoComida.Desayuno);
                var comIdx = tipos.IndexOf(TipoComida.Comida);
                Assert.True(desIdx < comIdx, "Desayuno should come before Comida");
            }

            if (tipos.Contains(TipoComida.Comida) && tipos.Contains(TipoComida.Cena))
            {
                var comIdx = tipos.IndexOf(TipoComida.Comida);
                var cenaIdx = tipos.IndexOf(TipoComida.Cena);
                Assert.True(comIdx < cenaIdx, "Comida should come before Cena");
            }
        }
    }

    // ─── Build simulated OCR words from real diet image ──────────────────

    /// <summary>
    /// Simulates what Tesseract would produce from the diet image.
    /// Layout: 7 columns (Dia 1-7) at x positions ~120-1060,
    ///         5 meal rows at y positions ~80, 250, 420, 650, 850
    /// Each cell contains food items with "name: qty" format.
    /// </summary>
    private static List<OcrWord> BuildRealDietImageWords()
    {
        var words = new List<OcrWord>();

        // Column X centers (roughly matching the real image layout)
        int[] colX = { 120, 260, 400, 540, 680, 820, 960 };
        int colW = 130; // column width for content

        // ── Day headers ──
        for (int d = 0; d < 7; d++)
        {
            words.Add(W("Dia", colX[d], 30, 40));
            words.Add(W($"{d + 1}", colX[d] + 45, 30, 15));
        }

        // ── Meal labels (left side, x=20) ──
        int desY = 80, tentY = 250, comY = 420, merY = 650, cenY = 850;
        words.Add(W("Desayuno", 20, desY, 90));
        words.Add(W("Tentempié", 20, tentY, 100));
        words.Add(W("1", 125, tentY, 15));
        words.Add(W("Comida", 20, comY, 70));
        words.Add(W("Merienda", 20, merY, 90));
        words.Add(W("1", 115, merY, 15));
        words.Add(W("Cena", 20, cenY, 50));

        // ── Desayuno foods (same for all days) ──
        for (int d = 0; d < 7; d++)
        {
            int x = colX[d];
            AddFoodWords(words, x, colW, desY + 30, "Bebida", "de", "soja", "con", "calcio:", "300g", "(1", "taza)");
            AddFoodWords(words, x, colW, desY + 55, "proteina", "de", "suero", "90%:", "40g");
            AddFoodWords(words, x, colW, desY + 80, "Canela:", "3g", "(Al", "gusto)");
            AddFoodWords(words, x, colW, desY + 105, "Nuez", "pelada:", "15g", "(3", "nueces)");
        }

        // ── Tentempié 1 foods (Dia 1) ──
        {
            int x = colX[0];
            AddFoodWords(words, x, colW, tentY + 30, "Aguacate/", "palta:", "200g", "(1", "pieza)");
            AddFoodWords(words, x, colW, tentY + 55, "Huevo", "de", "gallina:", "195g", "(3", "unidades", "talla", "M)");
            AddFoodWords(words, x, colW, tentY + 80, "Sal", "yodada:", "0g");
            AddFoodWords(words, x, colW, tentY + 105, "Bacon:", "40g", "(2", "lonchas)");
        }

        // ── Tentempié 1 foods (Dia 2) ──
        {
            int x = colX[1];
            AddFoodWords(words, x, colW, tentY + 30, "Tomate", "crudo:", "180g", "(1", "tomate", "mediano)");
            AddFoodWords(words, x, colW, tentY + 55, "Huevo", "de", "gallina:", "130g", "(2", "unidades", "talla", "M)");
            AddFoodWords(words, x, colW, tentY + 80, "Sal", "yodada:", "0g");
            AddFoodWords(words, x, colW, tentY + 105, "Queso", "manchego", "curado:", "30g", "(2", "cortadas", "finas)");
        }

        // ── Comida foods (Dia 1) ──
        {
            int x = colX[0];
            AddFoodWords(words, x, colW, comY + 30, "Champiñón", "o", "seta:", "200g-");
            AddFoodWords(words, x, colW, comY + 55, "Sal", "yodada:", "0g");
            AddFoodWords(words, x, colW, comY + 80, "Pollo,", "pechuga,", "solomillo:", "160g");
            AddFoodWords(words, x, colW, comY + 105, "Pan", "integral", "de", "trigo:", "30g-");
        }

        // ── Comida foods (Dia 2) ──
        {
            int x = colX[1];
            AddFoodWords(words, x, colW, comY + 30, "Patata:", "200g", "(1", "unidad", "mediana)");
            AddFoodWords(words, x, colW, comY + 55, "Sal", "yodada:", "0g");
            AddFoodWords(words, x, colW, comY + 80, "Aceite", "de", "oliva:", "10g", "(1", "cucharada", "sopera)");
            AddFoodWords(words, x, colW, comY + 105, "Filete", "de", "ternera:", "160g");
        }

        // ── Comida foods (Dia 3-7): add at least some content ──
        for (int d = 2; d < 7; d++)
        {
            int x = colX[d];
            AddFoodWords(words, x, colW, comY + 30, "Lechuga:", "60g", "(3", "hojas", "grandes)");
            AddFoodWords(words, x, colW, comY + 55, "Sal", "yodada:", "0g");
        }

        // ── Merienda 1 foods (Dia 1) ──
        {
            int x = colX[0];
            AddFoodWords(words, x, colW, merY + 30, "proteina", "de", "suero", "90%:", "40g");
            AddFoodWords(words, x, colW, merY + 55, "Canela:", "3g", "(Al", "gusto)");
            AddFoodWords(words, x, colW, merY + 80, "Agua", "mineral:", "300g");
            AddFoodWords(words, x, colW, merY + 105, "Almendra", "sin", "cáscara:", "8g", "(5", "almendras)");
            AddFoodWords(words, x, colW, merY + 130, "Naranja:", "230g", "(1", "unidad", "mediana)");
        }

        // ── Merienda 1 foods (Dia 2-7): some content ──
        for (int d = 1; d < 7; d++)
        {
            int x = colX[d];
            AddFoodWords(words, x, colW, merY + 30, "proteina", "de", "suero", "90%:", "40g");
            AddFoodWords(words, x, colW, merY + 55, "Canela:", "3g", "(Al", "gusto)");
            AddFoodWords(words, x, colW, merY + 80, "Agua", "mineral:", "300g");
        }

        // ── Cena foods (Dia 1) ──
        {
            int x = colX[0];
            AddFoodWords(words, x, colW, cenY + 30, "Pimiento", "rojo:", "60g", "(4", "rodajas)");
            AddFoodWords(words, x, colW, cenY + 55, "Sal", "yodada:", "0g");
            AddFoodWords(words, x, colW, cenY + 80, "Cebolla", "blanca:", "60g", "(1/2", "unidad", "pequeña)");
            AddFoodWords(words, x, colW, cenY + 105, "Pimiento", "verde:", "60g", "(4", "rodajas)");
            AddFoodWords(words, x, colW, cenY + 130, "Pollo,", "pechuga,", "solomillo:", "160g");
        }

        // ── Cena foods (Dia 2) ──
        {
            int x = colX[1];
            AddFoodWords(words, x, colW, cenY + 30, "Calabacín", "hervido:", "200g", "(1", "plato", "grande)");
            AddFoodWords(words, x, colW, cenY + 55, "Sal", "yodada:", "0g");
            AddFoodWords(words, x, colW, cenY + 80, "Aguacate/", "palta:", "200g", "(1", "pieza)");
            AddFoodWords(words, x, colW, cenY + 105, "Lomo", "adobado:", "160g");
        }

        // ── Cena foods (Dia 3-7): some content ──
        for (int d = 2; d < 7; d++)
        {
            int x = colX[d];
            AddFoodWords(words, x, colW, cenY + 30, "Pimiento", "rojo:", "60g", "(4", "rodajas)");
            AddFoodWords(words, x, colW, cenY + 55, "Sal", "yodada:", "0g");
        }

        return words;
    }

    /// <summary>
    /// Adds words at a given line Y position, spread horizontally within the column.
    /// </summary>
    private static void AddFoodWords(List<OcrWord> words, int colX, int colW, int y, params string[] tokens)
    {
        int spacing = colW / Math.Max(tokens.Length, 1);
        for (int i = 0; i < tokens.Length; i++)
        {
            int wordWidth = Math.Max(tokens[i].Length * 8, 20);
            words.Add(W(tokens[i], colX + i * spacing, y, wordWidth, 18, 90));
        }
    }
}
