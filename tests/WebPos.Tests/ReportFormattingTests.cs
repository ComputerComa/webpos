using System.Text;
using WebPos.Printing;
using WebPos.Reports;

namespace WebPos.Tests;

public class ReportFormattingTests
{
    private static readonly ReportHeader Header =
        new("Test Store", "ITEM SALES", ["Period: 2026-10-01 to 2026-10-29"], new DateTime(2026, 10, 29, 14, 5, 0));

    private static string[] Pages(string text) => text.TrimEnd('\f').Split('\f');

    private static string[] Lines(string page) => page.Split("\r\n")[..^1]; // every line ends with CR LF

    [Theory]
    [InlineData(80)]
    [InlineData(136)]
    public void Lines_never_exceed_width_and_are_ascii(int width)
    {
        var w = new TextReportWriter(new ReportLayout(width, 66, 3), Header);
        w.Columns(new("Item", 0), new("Category", 14), new("Barcode", 14), new("Qty", 8, Align.Right), new("Revenue", 12, Align.Right));
        for (var i = 0; i < 200; i++)
            w.Row(new string('X', 300) + " Crème brûlée – 1€", "Bakery", "1234567890123456789", "12", "1234.56");
        w.LeftRight(new string('L', 300), "right side");
        var text = w.Finish();

        Assert.All(text, ch => Assert.True(ch < 128, $"Non-ASCII char U+{(int)ch:X4}"));
        Assert.All(Pages(text).SelectMany(Lines), line => Assert.True(line.Length <= width, $"{line.Length} > {width}: {line}"));
    }

    [Fact]
    public void Pages_respect_length_and_repeat_headers()
    {
        var layout = new ReportLayout(80, 30, 3);
        var w = new TextReportWriter(layout, Header);
        w.Columns(new("Item", 0), new("Amount", 10, Align.Right));
        for (var i = 1; i <= 100; i++) w.Row($"Item {i}", i.ToString());
        var text = w.Finish();

        Assert.EndsWith("\f", text);
        var pages = Pages(text);
        Assert.True(pages.Length > 1);
        for (var p = 0; p < pages.Length; p++)
        {
            var lines = Lines(pages[p]);
            Assert.True(lines.Length <= layout.LinesPerPage - layout.BottomMargin, $"page {p + 1} has {lines.Length} lines");
            Assert.StartsWith("Test Store", lines[0]);
            Assert.EndsWith($"Page {p + 1}", lines[0]);
            Assert.StartsWith("ITEM SALES", lines[1]);
            Assert.StartsWith("Item", lines[4]); // column heading repeated after the page header
            Assert.Equal(new string('-', 80), lines[5]);
        }

        var rows = pages.SelectMany(Lines).Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^Item \d+ ")).ToList();
        Assert.Equal(100, rows.Count);
    }

    [Fact]
    public void Fill_column_takes_remaining_width_and_numbers_align_right()
    {
        var w = new TextReportWriter(new ReportLayout(80), Header);
        w.Columns(new("Item", 0), new("Amount", 10, Align.Right));
        w.Row("Coffee", "2.50");
        var line = Lines(Pages(w.Finish())[0]).Single(l => l.StartsWith("Coffee"));

        Assert.Equal(80, line.Length);
        Assert.EndsWith("      2.50", line);
    }

    [Fact]
    public void KeepTogether_moves_block_to_next_page()
    {
        var layout = new ReportLayout(80, 20, 0);
        var w = new TextReportWriter(layout, Header);
        while (w.RemainingLines > 3) w.Text("filler");
        w.KeepTogether(5);
        Assert.Equal(2, w.Page);
    }

    [Fact]
    public void Columns_wider_than_page_are_rejected()
    {
        var w = new TextReportWriter(new ReportLayout(80), Header);
        Assert.Throws<ArgumentException>(() => w.Columns(new("A", 50), new("B", 50)));
    }

    [Theory]
    [InlineData("Café Crème", "Cafe Creme")]
    [InlineData("Straße", "Strasse")]
    [InlineData("Price: 5€", "Price: 5EUR")]
    [InlineData("tab\there\u001b@", "tab here @")]
    [InlineData("日本", "??")]
    public void Ascii_sanitizer_folds_and_strips(string input, string expected)
    {
        Assert.Equal(expected, AsciiText.Sanitize(input));
    }

    [Fact]
    public async Task File_printer_writes_init_sequence_then_text()
    {
        var dir = Path.Combine(Path.GetTempPath(), "webpos-test-" + Guid.NewGuid());
        try
        {
            var options = new PrinterOptions { Transport = PrinterTransport.File, Path = dir, InitSequence = "1B40", Condensed = true };
            var printer = new FileReportPrinter(options);

            await printer.PrintAsync("Sales summary", "HELLO\r\n\f");

            var file = Assert.Single(Directory.GetFiles(dir, "*.prn"));
            Assert.EndsWith("-Sales-summary.prn", file);
            byte[] expected = [0x1B, 0x40, 0x0F, .. Encoding.ASCII.GetBytes("HELLO\r\n\f")];
            Assert.Equal(expected, await File.ReadAllBytesAsync(file));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
