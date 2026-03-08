using EatHealthyCycle.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace EatHealthyCycle.Tests;

public class ImageImportTsvParserTests
{
    private readonly ImageImportService _service;

    public ImageImportTsvParserTests()
    {
        var loggerMock = new Mock<ILogger<ImageImportService>>();
        // ImageImportService constructor takes (AppDbContext, ILogger<ImageImportService>)
        // We pass null for db since ParseTsvLines doesn't use it
        _service = new ImageImportService(null!, loggerMock.Object);
    }

    [Fact]
    public void ParseTsvLines_EmptyInput_ReturnsEmpty()
    {
        var lines = new[] { "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext" };
        var result = _service.ParseTsvLines(lines);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseTsvLines_SkipsNonWordLevels()
    {
        var lines = new[]
        {
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext",
            "1\t1\t0\t0\t0\t0\t0\t0\t1920\t1080\t-1\t",      // page level
            "2\t1\t1\t0\t0\t0\t100\t50\t500\t400\t-1\t",       // block level
            "3\t1\t1\t1\t0\t0\t100\t50\t500\t30\t-1\t",        // paragraph level
            "4\t1\t1\t1\t1\t0\t100\t50\t500\t30\t-1\t",        // line level
        };
        var result = _service.ParseTsvLines(lines);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseTsvLines_ParsesWordLevel5()
    {
        var lines = new[]
        {
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext",
            "5\t1\t1\t1\t1\t1\t100\t200\t80\t20\t96\tDesayuno",
            "5\t1\t1\t1\t1\t2\t190\t200\t120\t20\t92\tAvena",
        };
        var result = _service.ParseTsvLines(lines);
        Assert.Equal(2, result.Count);
        Assert.Equal("Desayuno", result[0].Text);
        Assert.Equal("Avena", result[1].Text);
        Assert.Equal(100, result[0].Left);
        Assert.Equal(200, result[0].Top);
        Assert.Equal(96, result[0].Confidence);
    }

    [Fact]
    public void ParseTsvLines_HandlesMissing12thColumn()
    {
        // Some Tesseract versions don't include trailing tab for empty text
        var lines = new[]
        {
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext",
            "1\t1\t0\t0\t0\t0\t0\t0\t1920\t1080\t-1",   // 11 columns - no text tab at all
            "2\t1\t1\t0\t0\t0\t100\t50\t500\t400\t-1",   // 11 columns
            "5\t1\t1\t1\t1\t1\t100\t200\t80\t20\t96\tLunes",  // 12 columns with text
        };
        var result = _service.ParseTsvLines(lines);
        Assert.Single(result);
        Assert.Equal("Lunes", result[0].Text);
    }

    [Fact]
    public void ParseTsvLines_SkipsVeryLowConfidence()
    {
        var lines = new[]
        {
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext",
            "5\t1\t1\t1\t1\t1\t100\t200\t80\t20\t3\tgarbage",    // conf 3 < 5 threshold
            "5\t1\t1\t1\t1\t2\t190\t200\t120\t20\t85\tBueno",      // conf 85 OK
        };
        var result = _service.ParseTsvLines(lines);
        Assert.Single(result);
        Assert.Equal("Bueno", result[0].Text);
    }

    [Fact]
    public void ParseTsvLines_HandlesNegativeConfidence()
    {
        // Tesseract uses -1 for structural elements, but level 5 should have real confidence
        var lines = new[]
        {
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext",
            "5\t1\t1\t1\t1\t1\t100\t200\t80\t20\t-1\tSomething",  // -1 conf should still be included
        };
        var result = _service.ParseTsvLines(lines);
        Assert.Single(result);
        Assert.Equal("Something", result[0].Text);
    }

    [Fact]
    public void ParseTsvLines_RealWorldTesseractOutput()
    {
        // Simulate real Tesseract output with mixed levels and Spanish text
        var lines = new[]
        {
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext",
            "1\t1\t0\t0\t0\t0\t0\t0\t800\t600\t-1\t",
            "2\t1\t1\t0\t0\t0\t50\t30\t700\t550\t-1\t",
            "3\t1\t1\t1\t0\t0\t50\t30\t200\t25\t-1\t",
            "4\t1\t1\t1\t1\t0\t50\t30\t200\t25\t-1\t",
            "5\t1\t1\t1\t1\t1\t50\t30\t100\t25\t95\tLunes",
            "5\t1\t1\t1\t1\t2\t200\t30\t100\t25\t93\tMartes",
            "5\t1\t1\t1\t1\t3\t350\t30\t100\t25\t91\tMiércoles",
            "3\t1\t1\t2\t0\t0\t50\t80\t200\t25\t-1\t",
            "4\t1\t1\t2\t1\t0\t50\t80\t200\t25\t-1\t",
            "5\t1\t1\t2\t1\t1\t50\t80\t100\t25\t90\tDesayuno",
            "3\t1\t1\t3\t0\t0\t50\t120\t200\t25\t-1\t",
            "4\t1\t1\t3\t1\t0\t50\t120\t200\t25\t-1\t",
            "5\t1\t1\t3\t1\t1\t50\t120\t150\t25\t88\tBebida",
            "5\t1\t1\t3\t1\t2\t210\t120\t30\t25\t85\tde",
            "5\t1\t1\t3\t1\t3\t250\t120\t60\t25\t87\tsoja:",
            "5\t1\t1\t3\t1\t4\t320\t120\t50\t25\t82\t300g",
        };

        var result = _service.ParseTsvLines(lines);

        // Should have: Lunes, Martes, Miércoles, Desayuno, Bebida, de, soja:, 300g = 8 words
        Assert.Equal(8, result.Count);
        Assert.Equal("Lunes", result[0].Text);
        Assert.Equal("Martes", result[1].Text);
        Assert.Equal("Miércoles", result[2].Text);
        Assert.Equal("Desayuno", result[3].Text);
        Assert.Equal("Bebida", result[4].Text);
        Assert.Equal("300g", result[7].Text);

        // Verify positions
        Assert.Equal(50, result[0].Left);  // Lunes
        Assert.Equal(30, result[0].Top);
        Assert.Equal(200, result[1].Left); // Martes - different column
    }

    [Fact]
    public void ParseTsvLines_HandlesLinesWith11Columns()
    {
        // Real scenario: structural lines have exactly 11 tab-separated fields
        var lines = new[]
        {
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext",
            "1\t1\t0\t0\t0\t0\t0\t0\t1920\t1080\t-1",    // 11 fields, no trailing empty
            "2\t1\t1\t0\t0\t0\t0\t0\t1920\t1080\t-1",    // 11 fields
            "3\t1\t1\t1\t0\t0\t0\t0\t1920\t30\t-1",      // 11 fields
            "4\t1\t1\t1\t1\t0\t0\t0\t1920\t30\t-1",      // 11 fields
            "5\t1\t1\t1\t1\t1\t100\t50\t80\t20\t96\tComida", // 12 fields with text
            "5\t1\t1\t1\t1\t2\t200\t50\t60\t20\t0\t",     // 12 fields, empty text (conf=0 but text empty)
            "5\t1\t1\t1\t1\t3\t280\t50\t90\t20\t94\tCena", // 12 fields with text
        };
        var result = _service.ParseTsvLines(lines);
        Assert.Equal(2, result.Count);
        Assert.Equal("Comida", result[0].Text);
        Assert.Equal("Cena", result[1].Text);
    }

    [Fact]
    public void ParseTsvLines_676ParseErrors_Scenario()
    {
        // Reproduce the exact scenario from production:
        // 783 lines total (1 header + 782 data), 676 had < 12 cols, rest had empty text
        // This was because ALL lines including level-5 words had only 11 columns
        // After fix: we filter by level=5 AND handle 11-column lines
        var lines = new List<string>
        {
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext"
        };

        // Add some structural lines with 11 columns (the 676 "parse errors")
        for (int i = 0; i < 10; i++)
            lines.Add($"1\t1\t{i}\t0\t0\t0\t0\t0\t800\t600\t-1");
        for (int i = 0; i < 10; i++)
            lines.Add($"2\t1\t{i}\t0\t0\t0\t0\t0\t800\t600\t-1");

        // Add word lines with 12 columns
        lines.Add("5\t1\t1\t1\t1\t1\t50\t30\t100\t25\t95\tLunes");
        lines.Add("5\t1\t1\t1\t1\t2\t200\t30\t100\t25\t93\tDesayuno");
        lines.Add("5\t1\t1\t1\t2\t1\t50\t80\t100\t25\t88\tAvena");

        var result = _service.ParseTsvLines(lines.ToArray());
        Assert.Equal(3, result.Count);
        Assert.Equal("Lunes", result[0].Text);
        Assert.Equal("Desayuno", result[1].Text);
        Assert.Equal("Avena", result[2].Text);
    }

    [Fact]
    public void ParseTsvLines_HandlesFloatConfidence()
    {
        // Production Tesseract outputs confidence as float (e.g. 42.886963)
        var lines = new[]
        {
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext",
            "5\t1\t1\t1\t1\t1\t143\t33\t198\t95\t49.362656\tDesayuno",
            "5\t1\t1\t1\t1\t2\t499\t37\t143\t65\t42.130310\tBebida",
            "5\t1\t1\t1\t1\t3\t792\t37\t143\t65\t92.886963\tAvena",
        };
        var result = _service.ParseTsvLines(lines);
        Assert.Equal(3, result.Count);
        Assert.Equal("Desayuno", result[0].Text);
        Assert.Equal(49, result[0].Confidence); // 49.36 rounds to 49
        Assert.Equal("Bebida", result[1].Text);
        Assert.Equal(42, result[1].Confidence);
        Assert.Equal("Avena", result[2].Text);
        Assert.Equal(93, result[2].Confidence); // 92.88 rounds to 93
    }

    [Fact]
    public void ParseTsvLines_FloatConfidence_StillFiltersLowConf()
    {
        var lines = new[]
        {
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext",
            "5\t1\t1\t1\t1\t1\t100\t200\t80\t20\t2.5\tgarbage",   // 2.5 rounds to 3, < 5 threshold
            "5\t1\t1\t1\t1\t2\t190\t200\t120\t20\t85.7\tBueno",   // 85.7 rounds to 86, OK
        };
        var result = _service.ParseTsvLines(lines);
        Assert.Single(result);
        Assert.Equal("Bueno", result[0].Text);
    }

    [Fact]
    public void ParseTsvLines_RealProductionTsvOutput()
    {
        // Exact format from the production logs that was failing
        var lines = new[]
        {
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext",
            "1\t1\t0\t0\t0\t0\t0\t0\t2358\t3098\t-1\t",
            "2\t1\t1\t0\t0\t0\t147\t37\t2060\t66\t-1\t",
            "3\t1\t1\t1\t0\t0\t147\t37\t2060\t66\t-1\t",
            "4\t1\t1\t1\t1\t0\t147\t37\t2060\t66\t-1\t",
            "5\t1\t1\t1\t1\t1\t143\t33\t198\t95\t49.362656\tDia",
            "5\t1\t1\t1\t1\t2\t499\t37\t143\t65\t42.130310\tDia",
            "5\t1\t1\t1\t1\t3\t792\t37\t143\t65\t42.886963\tDia",
            "5\t1\t1\t1\t1\t4\t1093\t37\t143\t65\t27.497826\tDia",
            "5\t1\t1\t1\t1\t5\t1393\t37\t144\t65\t61.918968\tDia",
            "5\t1\t2\t1\t1\t1\t50\t120\t100\t25\t88.5\tDesayuno",
            "5\t1\t3\t1\t1\t1\t150\t150\t120\t25\t75.3\tBebida",
            "5\t1\t3\t1\t1\t2\t280\t150\t30\t25\t70.1\tde",
            "5\t1\t3\t1\t1\t3\t320\t150\t50\t25\t65.8\tsoja",
        };
        var result = _service.ParseTsvLines(lines);
        // All level-5 entries with non-empty text and float confidence should parse
        Assert.Equal(9, result.Count);
        Assert.Equal("Dia", result[0].Text);
        Assert.Equal("Desayuno", result[5].Text);
        Assert.Equal("Bebida", result[6].Text);
    }
}
