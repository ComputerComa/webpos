using System.Globalization;
using System.Text.RegularExpressions;
using WebPos.Services;
using ZXing;
using ZXing.Common;

namespace WebPos.Tests;

public partial class BarcodeTests
{
    [GeneratedRegex("""<rect x="(\d+)" width="(\d+)" height="\d+" fill="#000"/>""")]
    private static partial Regex Bar();

    /// <summary>Turns the drawn SVG back into pixels and reads it with ZXing's decoder, like a scanner would.</summary>
    private static Result? Scan(string svg)
    {
        var width = int.Parse(Regex.Match(svg, """width="(\d+)" """.TrimEnd()).Groups[1].Value, CultureInfo.InvariantCulture);
        const int height = 30;
        var row = Enumerable.Repeat((byte)255, width).ToArray();
        foreach (Match bar in Bar().Matches(svg))
        {
            var (x, w) = (int.Parse(bar.Groups[1].Value), int.Parse(bar.Groups[2].Value));
            Array.Fill(row, (byte)0, x, w);
        }
        var pixels = Enumerable.Repeat(row, height).SelectMany(r => r).ToArray();

        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions
            {
                PossibleFormats = [BarcodeFormat.UPC_A, BarcodeFormat.EAN_13, BarcodeFormat.CODE_128],
                TryHarder = true,
            },
        };
        return reader.Decode(new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.Gray8));
    }

    [Theory]
    [InlineData("400000001104", "UPC-A", BarcodeFormat.UPC_A)]     // sample catalog item
    [InlineData("4006381333931", "EAN-13", BarcodeFormat.EAN_13)]
    [InlineData("PC1a2b3c4d", "Code 128", BarcodeFormat.CODE_128)] // test-style barcode
    [InlineData("400000001105", "Code 128", BarcodeFormat.CODE_128)] // wrong check digit: still drawn, as Code 128
    [InlineData("R000042", "Code 128", BarcodeFormat.CODE_128)]     // receipt number
    public void Drawn_barcodes_scan_back_to_the_same_code(string code, string symbology, BarcodeFormat format)
    {
        Assert.Equal(symbology, Barcodes.Encode(code)?.Symbology);

        var svg = Barcodes.Svg(code)!;
        var result = Scan(svg);

        Assert.NotNull(result);
        Assert.Equal(code, result.Text);
        Assert.Equal(format, result.BarcodeFormat);
        Assert.Contains($">{code}</text>", svg);
        Assert.StartsWith("""<svg xmlns="http://www.w3.org/2000/svg" class="barcode-svg" width=""", svg);
    }

    [Theory]
    [InlineData("4011")]   // produce codes are typed in
    [InlineData("94011")]
    [InlineData("")]
    [InlineData("Café")]   // Code 128 can't carry it
    public void Codes_without_a_barcode_return_null(string code)
    {
        Assert.Null(Barcodes.Svg(code));
    }

    [Fact]
    public void Text_under_the_bars_is_escaped()
    {
        Assert.Contains(">A&lt;B&amp;C</text>", Barcodes.Svg("A<B&C")!);
    }
}
