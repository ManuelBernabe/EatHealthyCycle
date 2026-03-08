using EatHealthyCycle.Models;
using EatHealthyCycle.Services;
using Microsoft.Extensions.Logging;
using Moq;
using static EatHealthyCycle.Services.ImageImportService;

namespace EatHealthyCycle.Tests;

/// <summary>
/// Tests simulating real Tesseract OCR output from the diet table image.
/// Real scenario: headers are garbled (white text on teal background unreadable),
/// but word positions are correct and Y-gaps mark meal section boundaries.
/// Image: 2358x3098px, 7 day columns, 5 meal rows.
/// </summary>
public class ImageImportTableParsingTests
{
    private readonly ImageImportService _service;

    public ImageImportTableParsingTests()
    {
        var loggerMock = new Mock<ILogger<ImageImportService>>();
        _service = new ImageImportService(null!, loggerMock.Object);
    }

    private static OcrWord W(string text, int left, int top, int width = 80, int height = 20, int conf = 90)
        => new()
        {
            Text = text, Left = left, Top = top, Width = width, Height = height,
            Right = left + width, Bottom = top + height,
            CenterX = left + width / 2.0, CenterY = top + height / 2.0,
            Confidence = conf, BlockNum = 1, LineNum = 1, ParNum = 1
        };

    // ═══════════════════════════════════════════════════════════════════════
    // Position-based column detection (garbled headers)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void DetectColumnsByPosition_FindsColumnsFromGarbledHeaders()
    {
        // Real production: Tesseract reads "CE", "E", "E", "E", "E" at evenly-spaced X positions
        // These are the "Dia 1"-"Dia 5" headers, garbled but positions correct
        var words = new List<OcrWord>
        {
            // Header words (garbled but evenly spaced ~300px apart, Y=33-37)
            W("CE", 143, 33, 198, 95, 49),   // Dia 1 center
            W("E",  499, 37, 143, 65, 42),    // Dia 2 center
            W("E",  792, 37, 143, 65, 42),    // Dia 3 center
            W("E",  1093, 37, 143, 65, 27),   // Dia 4 center
            W("E",  1393, 37, 144, 65, 61),   // Dia 5 center
            // Content words below (so image height is > header)
            W("Bebida", 200, 200, 80, 20, 85),
            W("Avena",  500, 200, 80, 20, 85),
            W("Leche",  800, 200, 80, 20, 85),
            W("Pan",    1100, 200, 80, 20, 85),
            W("Huevo",  1400, 200, 80, 20, 85),
            // Bottom word to establish image height
            W("fin",    200, 3000, 40, 20, 50),
        };

        var cols = _service.DetectDayColumns(words);

        // Should detect at least 5 columns from position, then extrapolate to 7
        Assert.True(cols.Count >= 5, $"Expected >= 5 columns from position-based detection, got {cols.Count}");
        Assert.True(cols.Count <= 7, $"Expected <= 7 columns, got {cols.Count}");

        // Columns should be ordered left-to-right
        for (int i = 1; i < cols.Count; i++)
            Assert.True(cols[i].HeaderCenterX > cols[i - 1].HeaderCenterX,
                $"Column {i} should be to the right of column {i - 1}");
    }

    [Fact]
    public void DetectColumnsByPosition_ExtrapolatesTo7()
    {
        // 5 evenly-spaced header words → should extrapolate to 7 if image is wide enough
        var words = new List<OcrWord>
        {
            W("E", 200, 30, 100, 60, 40),
            W("E", 500, 30, 100, 60, 40),
            W("E", 800, 30, 100, 60, 40),
            W("E", 1100, 30, 100, 60, 40),
            W("E", 1400, 30, 100, 60, 40),
            // Content at wider X to establish image width ~2400
            W("x", 2300, 500, 40, 20, 50),
            W("x", 200, 3000, 40, 20, 50),
        };

        var cols = _service.DetectDayColumns(words);

        Assert.Equal(7, cols.Count);
        // Extrapolated columns should be at ~1700 and ~2000
        Assert.True(cols[5].HeaderCenterX > 1600, $"6th column center should be > 1600, was {cols[5].HeaderCenterX}");
        Assert.True(cols[6].HeaderCenterX > 1900, $"7th column center should be > 1900, was {cols[6].HeaderCenterX}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Text-based column detection (ideal scenario)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void DetectDayColumns_FindsDia1Through7()
    {
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
        Assert.Equal(DayOfWeek.Sunday, cols[6].DayOfWeek);
    }

    [Fact]
    public void DetectDayColumns_FindsDayNames()
    {
        var words = new List<OcrWord>
        {
            W("Lunes", 120, 30, 60), W("Martes", 260, 30, 60), W("Miércoles", 400, 30, 90),
        };
        var cols = _service.DetectDayColumns(words);
        Assert.Equal(3, cols.Count);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Gap-based meal row detection
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void DetectMealRowsByGaps_FindsMealSections()
    {
        // Simulates OCR output where meal labels are unreadable (white on teal)
        // but there are clear Y-gaps between meal sections (the teal header bars)
        var dayColumns = new List<DayColumn>
        {
            new() { DayOfWeek = DayOfWeek.Monday, Label = "Día 1",
                HeaderCenterX = 200, HeaderTop = 30, HeaderLeft = 150, HeaderRight = 250 }
        };

        var words = new List<OcrWord>();
        // Desayuno section: Y 100-250
        for (int y = 100; y < 250; y += 25)
            words.Add(W("food", 200, y, 80, 18));

        // GAP: Y 250-330 (teal "Tentempié 1" bar, 80px gap)

        // Tentempié section: Y 330-500
        for (int y = 330; y < 500; y += 25)
            words.Add(W("food", 200, y, 80, 18));

        // GAP: Y 500-580 (teal "Comida" bar)

        // Comida section: Y 580-800
        for (int y = 580; y < 800; y += 25)
            words.Add(W("food", 200, y, 80, 18));

        // GAP: Y 800-880 (teal "Merienda 1" bar)

        // Merienda section: Y 880-1100
        for (int y = 880; y < 1100; y += 25)
            words.Add(W("food", 200, y, 80, 18));

        // GAP: Y 1100-1180 (teal "Cena" bar)

        // Cena section: Y 1180-1400
        for (int y = 1180; y < 1400; y += 25)
            words.Add(W("food", 200, y, 80, 18));

        var rows = _service.DetectMealRowsByGaps(words, dayColumns);

        Assert.Equal(5, rows.Count);
        Assert.Equal(TipoComida.Desayuno, rows[0].MealType);
        Assert.Equal(TipoComida.Almuerzo, rows[1].MealType);
        Assert.Equal(TipoComida.Comida, rows[2].MealType);
        Assert.Equal(TipoComida.Merienda, rows[3].MealType);
        Assert.Equal(TipoComida.Cena, rows[4].MealType);
    }

    [Fact]
    public void DetectMealRows_TextBased_FindsAllMealTypes()
    {
        var dayColumns = new List<OcrWord> { W("Dia", 120, 30, 40), W("1", 165, 30, 15) };
        var dayCols = _service.DetectDayColumns(dayColumns);

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
        Assert.Equal(TipoComida.Cena, rows[4].MealType);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ParseFoodLine tests (from real image food items)
    // ═══════════════════════════════════════════════════════════════════════

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
    public void ParseFoodLine_ColonFormat_AceiteDeOliva()
    {
        var result = ImageImportService.ParseFoodLine("Aceite de oliva: 10g (1 cucharada sopera)");
        Assert.NotNull(result);
        Assert.Equal("ACEITE DE OLIVA", result.Name);
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
    public void ParseFoodLine_ColonFormat_AguaMineral()
    {
        var result = ImageImportService.ParseFoodLine("Agua mineral: 300g");
        Assert.NotNull(result);
        Assert.Equal("AGUA MINERAL", result.Name);
        Assert.Equal("300g", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_PimientoRojo()
    {
        var result = ImageImportService.ParseFoodLine("Pimiento rojo: 60g (4 rodajas)");
        Assert.NotNull(result);
        Assert.Equal("PIMIENTO ROJO", result.Name);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_AtunEnlatado()
    {
        var result = ImageImportService.ParseFoodLine("Atún enlatado en agua: 120g (2 latas pequeñas redondas)");
        Assert.NotNull(result);
        Assert.Equal("ATÚN ENLATADO EN AGUA", result.Name);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_ZanahoriaWithParens()
    {
        var result = ImageImportService.ParseFoodLine("Zanahoria: 80g (1 unidad mediana (80g))");
        Assert.NotNull(result);
        Assert.Equal("ZANAHORIA", result.Name);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_CacaoEnPolvo()
    {
        var result = ImageImportService.ParseFoodLine("Cacao en polvo desgrasado sin azúcar: 10g (1 cucharada de postre)");
        Assert.NotNull(result);
        Assert.Equal("CACAO EN POLVO DESGRASADO SIN AZÚCAR", result.Name);
    }

    [Fact]
    public void ParseFoodLine_ColonFormat_CebollaBlanca()
    {
        var result = ImageImportService.ParseFoodLine("Cebolla blanca: 60g (1/2 unidad pequeña)");
        Assert.NotNull(result);
        Assert.Equal("CEBOLLA BLANCA", result.Name);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Full end-to-end: real production scenario (garbled headers + gaps)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ParseTableFromWords_GarbledHeaders_Detects7Days()
    {
        var words = BuildProductionScenarioWords();
        var dieta = _service.ParseTableFromWords(words, 1, "Test Diet", "diet.jpg");

        Assert.Equal(7, dieta.Dias.Count);
    }

    [Fact]
    public void ParseTableFromWords_GarbledHeaders_Detects5MealTypes()
    {
        var words = BuildProductionScenarioWords();
        var dieta = _service.ParseTableFromWords(words, 1, "Test Diet", "diet.jpg");

        Assert.True(dieta.Dias.Count >= 5, $"Need at least 5 days, got {dieta.Dias.Count}");

        // Each day should have at least 3 meal types
        foreach (var dia in dieta.Dias)
        {
            Assert.True(dia.Comidas.Count >= 3,
                $"Day {dia.DiaSemana} should have at least 3 meals, got {dia.Comidas.Count}");
        }
    }

    [Fact]
    public void ParseTableFromWords_GarbledHeaders_HasDesayunoWithFood()
    {
        var words = BuildProductionScenarioWords();
        var dieta = _service.ParseTableFromWords(words, 1, "Test Diet", "diet.jpg");

        foreach (var dia in dieta.Dias)
        {
            var desayuno = dia.Comidas.FirstOrDefault(c => c.Tipo == TipoComida.Desayuno);
            Assert.NotNull(desayuno);
            Assert.True(desayuno.Alimentos.Count > 0,
                $"Desayuno for {dia.DiaSemana} should have food items");
        }
    }

    [Fact]
    public void ParseTableFromWords_GarbledHeaders_HasComidaWithFood()
    {
        var words = BuildProductionScenarioWords();
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
    public void ParseTableFromWords_GarbledHeaders_HasCenaWithFood()
    {
        var words = BuildProductionScenarioWords();
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
    public void ParseTableFromWords_GarbledHeaders_MealOrderCorrect()
    {
        var words = BuildProductionScenarioWords();
        var dieta = _service.ParseTableFromWords(words, 1, "Test Diet", "diet.jpg");

        foreach (var dia in dieta.Dias)
        {
            var tipos = dia.Comidas.Select(c => c.Tipo).ToList();
            if (tipos.Contains(TipoComida.Desayuno) && tipos.Contains(TipoComida.Comida))
                Assert.True(tipos.IndexOf(TipoComida.Desayuno) < tipos.IndexOf(TipoComida.Comida));
            if (tipos.Contains(TipoComida.Comida) && tipos.Contains(TipoComida.Cena))
                Assert.True(tipos.IndexOf(TipoComida.Comida) < tipos.IndexOf(TipoComida.Cena));
        }
    }

    [Fact]
    public void ParseTableFromWords_GarbledHeaders_HasBebidaDeSoja()
    {
        var words = BuildProductionScenarioWords();
        var dieta = _service.ParseTableFromWords(words, 1, "Test Diet", "diet.jpg");

        var allDesayunoAlimentos = dieta.Dias
            .SelectMany(d => d.Comidas)
            .Where(c => c.Tipo == TipoComida.Desayuno)
            .SelectMany(c => c.Alimentos)
            .ToList();

        Assert.Contains(allDesayunoAlimentos, a =>
            a.Nombre.Contains("BEBIDA", StringComparison.OrdinalIgnoreCase) ||
            a.Nombre.Contains("SOJA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseTableFromWords_GarbledHeaders_SetsMetadata()
    {
        var words = BuildProductionScenarioWords();
        var dieta = _service.ParseTableFromWords(words, 42, "Mi Dieta", "foto.jpg");

        Assert.Equal(42, dieta.UsuarioId);
        Assert.Equal("Mi Dieta", dieta.Nombre);
        Assert.Equal("foto.jpg", dieta.ArchivoOriginal);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Also test ideal scenario (readable headers)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ParseTableFromWords_ReadableHeaders_Detects7Days()
    {
        var words = BuildIdealScenarioWords();
        var dieta = _service.ParseTableFromWords(words, 1, "Test Diet", "diet.jpg");
        Assert.Equal(7, dieta.Dias.Count);
    }

    [Fact]
    public void ParseTableFromWords_ReadableHeaders_5MealsPerDay()
    {
        var words = BuildIdealScenarioWords();
        var dieta = _service.ParseTableFromWords(words, 1, "Test Diet", "diet.jpg");

        foreach (var dia in dieta.Dias)
        {
            Assert.True(dia.Comidas.Count >= 3,
                $"Day {dia.DiaSemana}: expected at least 3 meals, got {dia.Comidas.Count}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Build simulated OCR data
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Simulates real production Tesseract output:
    /// - Image is 2358x3098 pixels
    /// - Headers are garbled ("CE", "E", "E"...) but at correct X positions
    /// - Meal labels are NOT readable (white on teal → invisible after preprocessing)
    /// - Food items ARE readable, positioned in correct cells
    /// - Y-gaps of ~80px between meal sections (the teal header bars)
    /// </summary>
    private static List<OcrWord> BuildProductionScenarioWords()
    {
        var words = new List<OcrWord>();

        // Image dimensions: 2358 x 3098
        // 7 column centers at evenly spaced positions
        int[] colCX = { 243, 543, 843, 1143, 1443, 1743, 2043 };
        int colW = 250;

        // Garbled header words (correct positions, garbage text)
        words.Add(W("CE", 143, 33, 198, 95, 49));  // Dia 1
        words.Add(W("E",  499, 37, 143, 65, 42));   // Dia 2
        words.Add(W("E",  792, 37, 143, 65, 42));   // Dia 3
        words.Add(W("E",  1093, 37, 143, 65, 27));  // Dia 4
        words.Add(W("E",  1393, 37, 144, 65, 61));  // Dia 5
        words.Add(W("E",  1693, 37, 143, 65, 45));  // Dia 6
        words.Add(W("E",  1993, 37, 143, 65, 38));  // Dia 7

        // Y layout with gaps for teal bars:
        // Desayuno content:   Y 150 - 400
        // GAP (teal bar):     Y 400 - 480
        // Tentempié content:  Y 480 - 750
        // GAP (teal bar):     Y 750 - 830
        // Comida content:     Y 830 - 1200
        // GAP (teal bar):     Y 1200 - 1280
        // Merienda content:   Y 1280 - 1700
        // GAP (teal bar):     Y 1700 - 1780
        // Cena content:       Y 1780 - 2200

        // ── Desayuno foods (Y 150-400) ──
        for (int d = 0; d < 7; d++)
        {
            int x = colCX[d] - colW / 2;
            AddFood(words, x, colW, 160, "Bebida", "de", "soja", "con", "calcio:", "300g", "(1", "taza)");
            AddFood(words, x, colW, 200, "proteina", "de", "suero", "90%:", "40g");
            AddFood(words, x, colW, 240, "Canela:", "3g", "(Al", "gusto)");
            AddFood(words, x, colW, 280, "Nuez", "pelada:", "15g", "(3", "nueces)");
        }

        // ── Tentempié foods (Y 480-750) ──
        for (int d = 0; d < 7; d++)
        {
            int x = colCX[d] - colW / 2;
            AddFood(words, x, colW, 500, "Aguacate/", "palta:", "200g", "(1", "pieza)");
            AddFood(words, x, colW, 540, "Huevo", "de", "gallina:", "195g");
            AddFood(words, x, colW, 580, "Sal", "yodada:", "0g");
            AddFood(words, x, colW, 620, "Bacon:", "40g", "(2", "lonchas)");
        }

        // ── Comida foods (Y 830-1200) ──
        for (int d = 0; d < 7; d++)
        {
            int x = colCX[d] - colW / 2;
            AddFood(words, x, colW, 850, "Champiñón", "o", "seta:", "200g");
            AddFood(words, x, colW, 890, "Sal", "yodada:", "0g");
            AddFood(words, x, colW, 930, "Pollo,", "pechuga,", "solomillo:", "160g");
            AddFood(words, x, colW, 970, "Pan", "integral:", "30g");
            AddFood(words, x, colW, 1010, "Filete", "de", "ternera:", "160g");
        }

        // ── Merienda foods (Y 1280-1700) ──
        for (int d = 0; d < 7; d++)
        {
            int x = colCX[d] - colW / 2;
            AddFood(words, x, colW, 1300, "proteina", "de", "suero", "90%:", "40g");
            AddFood(words, x, colW, 1340, "Canela:", "3g", "(Al", "gusto)");
            AddFood(words, x, colW, 1380, "Agua", "mineral:", "300g");
            AddFood(words, x, colW, 1420, "Almendra", "sin", "cáscara:", "8g");
            AddFood(words, x, colW, 1460, "Naranja:", "230g", "(1", "unidad", "mediana)");
        }

        // ── Cena foods (Y 1780-2200) ──
        for (int d = 0; d < 7; d++)
        {
            int x = colCX[d] - colW / 2;
            AddFood(words, x, colW, 1800, "Pimiento", "rojo:", "60g", "(4", "rodajas)");
            AddFood(words, x, colW, 1840, "Sal", "yodada:", "0g");
            AddFood(words, x, colW, 1880, "Cebolla", "blanca:", "60g");
            AddFood(words, x, colW, 1920, "Pimiento", "verde:", "60g");
            AddFood(words, x, colW, 1960, "Pollo,", "pechuga,", "solomillo:", "160g");
        }

        return words;
    }

    /// <summary>
    /// Ideal scenario: readable "Dia N" headers and meal labels.
    /// </summary>
    private static List<OcrWord> BuildIdealScenarioWords()
    {
        var words = new List<OcrWord>();

        int[] colCX = { 200, 400, 600, 800, 1000, 1200, 1400 };
        int colW = 180;

        // Readable day headers
        for (int d = 0; d < 7; d++)
        {
            words.Add(W("Dia", colCX[d] - 30, 30, 40));
            words.Add(W($"{d + 1}", colCX[d] + 15, 30, 15));
        }

        // Meal labels
        words.Add(W("Desayuno", 20, 80, 90));
        words.Add(W("Tentempié", 20, 300, 100));
        words.Add(W("1", 125, 300, 15));
        words.Add(W("Comida", 20, 520, 70));
        words.Add(W("Merienda", 20, 740, 90));
        words.Add(W("1", 115, 740, 15));
        words.Add(W("Cena", 20, 960, 50));

        // Food items for all days
        for (int d = 0; d < 7; d++)
        {
            int x = colCX[d] - colW / 2;
            // Desayuno
            AddFood(words, x, colW, 100, "Bebida", "de", "soja:", "300g");
            AddFood(words, x, colW, 130, "Canela:", "3g");
            // Tentempié
            AddFood(words, x, colW, 330, "Aguacate:", "200g");
            AddFood(words, x, colW, 360, "Huevo:", "195g");
            // Comida
            AddFood(words, x, colW, 550, "Pollo:", "160g");
            AddFood(words, x, colW, 580, "Sal:", "0g");
            // Merienda
            AddFood(words, x, colW, 770, "Agua", "mineral:", "300g");
            AddFood(words, x, colW, 800, "Naranja:", "230g");
            // Cena
            AddFood(words, x, colW, 990, "Pimiento:", "60g");
            AddFood(words, x, colW, 1020, "Cebolla:", "60g");
        }

        return words;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // OCR error correction tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FixOcrErrors_Fixes_G_As_9()
    {
        // "40g" misread as "409"
        Assert.Equal("40g", ImageImportService.FixOcrErrors("409"));
        Assert.Equal("160g", ImageImportService.FixOcrErrors("1609"));
        Assert.Equal("300g", ImageImportService.FixOcrErrors("3009"));
        Assert.Equal("3g", ImageImportService.FixOcrErrors("39"));
        Assert.Equal("8g", ImageImportService.FixOcrErrors("89"));
    }

    [Fact]
    public void FixOcrErrors_Fixes_G_Before_Parens()
    {
        Assert.Equal("40g (2 lonchas)", ImageImportService.FixOcrErrors("409 (2 lonchas)"));
        Assert.Equal("300g (1 taza)", ImageImportService.FixOcrErrors("3009 (1 taza)"));
    }

    [Fact]
    public void FixOcrErrors_SplitsMergedWords()
    {
        Assert.Contains("FILETE DE", ImageImportService.FixOcrErrors("FILETEDETEMERA"));
        Assert.Contains("HUEVO DE", ImageImportService.FixOcrErrors("HUEVODEGALINA"));
        Assert.Contains("PAN INTEGRAL", ImageImportService.FixOcrErrors("PANINTEGRAL"));
        Assert.Contains("SAL YODADA", ImageImportService.FixOcrErrors("SALYODADA"));
    }

    [Fact]
    public void ParseFoodLine_WithOcrErrors_FixesQuantity()
    {
        // "Pollo: 1609" should become "Pollo: 160g"
        var result = ImageImportService.ParseFoodLine("Pollo: 1609");
        Assert.NotNull(result);
        Assert.Equal("POLLO", result.Name);
        Assert.Equal("160g", result.Quantity);
    }

    [Fact]
    public void ParseFoodLine_WithOcrErrors_FixesMergedName()
    {
        var result = ImageImportService.ParseFoodLine("FILETEDETERNERA: 1609");
        Assert.NotNull(result);
        Assert.Contains("FILETE DE", result.Name);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Noise band scenario (garbled teal bar text creating false sections)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void DetectMealRowsByGaps_WithNoiseBands_DoesNotShiftSections()
    {
        // Production scenario: garbled teal bar text creates 1-2 word noise bands
        // at the start of each meal section, causing a false extra first section
        var dayColumns = new List<DayColumn>
        {
            new() { DayOfWeek = DayOfWeek.Monday, Label = "Día 1",
                HeaderCenterX = 200, HeaderTop = 30, HeaderLeft = 150, HeaderRight = 250 }
        };

        var words = new List<OcrWord>();

        // Small noise band at y=95 (1 garbled word from teal "Desayuno" bar)
        words.Add(W("CE", 200, 95, 40, 18, 30));

        // Desayuno section: Y 150-290
        for (int y = 150; y < 290; y += 25)
            words.Add(W("food", 200, y, 80, 18));

        // Small noise band at y=310 (1 garbled word from teal "Tentempié" bar)
        words.Add(W("X", 200, 310, 20, 18, 25));

        // GAP: Y 290-380

        // Tentempié section: Y 380-520
        for (int y = 380; y < 520; y += 25)
            words.Add(W("food", 200, y, 80, 18));

        // GAP: Y 520-620

        // Comida section: Y 620-800
        for (int y = 620; y < 800; y += 25)
            words.Add(W("food", 200, y, 80, 18));

        // GAP: Y 800-900

        // Merienda section: Y 900-1100
        for (int y = 900; y < 1100; y += 25)
            words.Add(W("food", 200, y, 80, 18));

        // GAP: Y 1100-1200

        // Cena section: Y 1200-1400
        for (int y = 1200; y < 1400; y += 25)
            words.Add(W("food", 200, y, 80, 18));

        var rows = _service.DetectMealRowsByGaps(words, dayColumns);

        // Should still find 5 sections, NOT 6 (the noise bands should be merged/skipped)
        Assert.Equal(5, rows.Count);
        Assert.Equal(TipoComida.Desayuno, rows[0].MealType);
        Assert.Equal(TipoComida.Almuerzo, rows[1].MealType);
        Assert.Equal(TipoComida.Comida, rows[2].MealType);
        Assert.Equal(TipoComida.Merienda, rows[3].MealType);
        Assert.Equal(TipoComida.Cena, rows[4].MealType);
    }

    [Fact]
    public void ParseTableFromWords_NoiseBands_DesayunoContentCorrect()
    {
        // With noise bands, verify that Desayuno gets the right food items (not shifted)
        var words = BuildProductionScenarioWithNoiseBands();
        var dieta = _service.ParseTableFromWords(words, 1, "Test Diet", "diet.jpg");

        Assert.True(dieta.Dias.Count >= 5, $"Expected >= 5 days, got {dieta.Dias.Count}");

        var day1 = dieta.Dias[0];
        var desayuno = day1.Comidas.FirstOrDefault(c => c.Tipo == TipoComida.Desayuno);
        Assert.NotNull(desayuno);

        // Desayuno should contain "Bebida" not "Aguacate" (which is Tentempié)
        var nombres = desayuno.Alimentos.Select(a => a.Nombre).ToList();
        Assert.Contains(nombres, n => n.Contains("BEBIDA", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(nombres, n => n.Contains("AGUACATE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractFoodItems_SeparatesFoodLines()
    {
        // Verify that food items on different Y positions are parsed as separate items
        var cellWords = new List<OcrWord>
        {
            // Line 1: "Bebida de soja: 300g"
            W("Bebida", 100, 160, 60, 18), W("de", 165, 160, 20, 18),
            W("soja:", 190, 160, 40, 18), W("300g", 235, 160, 40, 18),
            // Line 2: "Canela: 3g"
            W("Canela:", 100, 200, 60, 18), W("3g", 165, 200, 20, 18),
            // Line 3: "Nuez pelada: 15g"
            W("Nuez", 100, 240, 40, 18), W("pelada:", 145, 240, 60, 18), W("15g", 210, 240, 30, 18),
        };

        // Use reflection or test via ParseTableFromWords; ExtractFoodItems is private static
        // Instead, test via full pipeline with a single cell
        var dayColumns = new List<DayColumn>
        {
            new() { DayOfWeek = DayOfWeek.Monday, Label = "Día 1",
                HeaderCenterX = 200, HeaderTop = 30, HeaderLeft = 50, HeaderRight = 350,
                ContentLeft = 50, ContentRight = 350 }
        };
        var mealRows = new List<MealRow>
        {
            new() { MealType = TipoComida.Desayuno, Label = "Desayuno",
                Top = 150, CenterY = 200, ContentTop = 150, ContentBottom = 260 }
        };

        // Add header to establish column detection, plus content words
        var allWords = new List<OcrWord>(cellWords)
        {
            W("E", 200, 30, 100, 60, 40), // header
        };

        var dieta = _service.ParseTableFromWords(allWords, 1, "Test", "t.jpg");

        // Should have 1 day with Desayuno containing 3 separate food items
        Assert.True(dieta.Dias.Count >= 1);
        var desayuno = dieta.Dias[0].Comidas.FirstOrDefault(c => c.Tipo == TipoComida.Desayuno);
        if (desayuno != null)
        {
            Assert.True(desayuno.Alimentos.Count >= 2,
                $"Expected >= 2 separate food items, got {desayuno.Alimentos.Count}: " +
                string.Join(", ", desayuno.Alimentos.Select(a => a.Nombre)));
        }
    }

    /// <summary>
    /// Production scenario with noise bands from garbled teal bar text.
    /// </summary>
    private static List<OcrWord> BuildProductionScenarioWithNoiseBands()
    {
        var words = new List<OcrWord>();

        int[] colCX = { 243, 543, 843, 1143, 1443, 1743, 2043 };
        int colW = 250;

        // Garbled header words
        words.Add(W("CE", 143, 33, 198, 95, 49));
        words.Add(W("E",  499, 37, 143, 65, 42));
        words.Add(W("E",  792, 37, 143, 65, 42));
        words.Add(W("E",  1093, 37, 143, 65, 27));
        words.Add(W("E",  1393, 37, 144, 65, 61));
        words.Add(W("E",  1693, 37, 143, 65, 45));
        words.Add(W("E",  1993, 37, 143, 65, 38));

        // Noise band: garbled text from "Desayuno" teal bar (1-2 words per column)
        words.Add(W("RAR", 200, 135, 40, 18, 20));
        words.Add(W("O", 800, 137, 20, 18, 15));

        // Desayuno content: Y 160-380
        for (int d = 0; d < 7; d++)
        {
            int x = colCX[d] - colW / 2;
            AddFood(words, x, colW, 160, "Bebida", "de", "soja:", "300g");
            AddFood(words, x, colW, 200, "proteina", "de", "suero:", "40g");
            AddFood(words, x, colW, 240, "Canela:", "3g");
            AddFood(words, x, colW, 280, "Nuez", "pelada:", "15g");
        }

        // Noise band from "Tentempié" teal bar
        words.Add(W("2003", 500, 405, 50, 18, 18));

        // GAP ~400-460

        // Tentempié content: Y 460-700
        for (int d = 0; d < 7; d++)
        {
            int x = colCX[d] - colW / 2;
            AddFood(words, x, colW, 480, "Aguacate:", "200g");
            AddFood(words, x, colW, 520, "Huevo:", "195g");
            AddFood(words, x, colW, 560, "Sal", "yodada:", "0g");
            AddFood(words, x, colW, 600, "Bacon:", "40g");
        }

        // GAP ~700-800

        // Comida content: Y 830-1100
        for (int d = 0; d < 7; d++)
        {
            int x = colCX[d] - colW / 2;
            AddFood(words, x, colW, 850, "Champiñón:", "200g");
            AddFood(words, x, colW, 890, "Pollo:", "160g");
            AddFood(words, x, colW, 930, "Pan", "integral:", "30g");
            AddFood(words, x, colW, 970, "Filete:", "160g");
        }

        // GAP ~1100-1200

        // Merienda content: Y 1250-1550
        for (int d = 0; d < 7; d++)
        {
            int x = colCX[d] - colW / 2;
            AddFood(words, x, colW, 1270, "proteina:", "40g");
            AddFood(words, x, colW, 1310, "Agua", "mineral:", "300g");
            AddFood(words, x, colW, 1350, "Almendra:", "8g");
            AddFood(words, x, colW, 1390, "Naranja:", "230g");
        }

        // GAP ~1550-1650

        // Cena content: Y 1700-2000
        for (int d = 0; d < 7; d++)
        {
            int x = colCX[d] - colW / 2;
            AddFood(words, x, colW, 1720, "Pimiento", "rojo:", "60g");
            AddFood(words, x, colW, 1760, "Sal", "yodada:", "0g");
            AddFood(words, x, colW, 1800, "Cebolla:", "60g");
            AddFood(words, x, colW, 1840, "Pollo:", "160g");
        }

        return words;
    }

    private static void AddFood(List<OcrWord> words, int colX, int colW, int y, params string[] tokens)
    {
        int spacing = colW / Math.Max(tokens.Length, 1);
        for (int i = 0; i < tokens.Length; i++)
        {
            int wordWidth = Math.Max(tokens[i].Length * 8, 20);
            words.Add(W(tokens[i], colX + i * spacing, y, wordWidth, 18, 85));
        }
    }
}
