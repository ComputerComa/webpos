using System.Globalization;
using System.Net;
using System.Text;
using ZXing.OneD;

namespace WebPos.Services;

/// <summary>
/// Draws item barcodes as SVG, picking the symbology a store would use: UPC-A for 12 digits, EAN-13 for 13,
/// Code 128 for anything else. ZXing works out the bar pattern; the drawing is done here so every bar is a whole
/// number of pixels, which keeps the edges sharp enough for a scanner to read off a screen.
/// </summary>
public static class Barcodes
{
    /// <summary>Blank modules on each side; scanners need a quiet zone to find the start and end.</summary>
    public const int QuietZone = 10;

    /// <summary>4- and 5-digit produce codes (PLUs) have no barcode at a real till; the cashier types them.</summary>
    public static bool IsPlu(string code) => code.Length is 4 or 5 && code.All(char.IsAsciiDigit);

    /// <summary>The bar pattern (true = bar) and symbology name, or null if the code can't be drawn as a barcode.</summary>
    public static (string Symbology, bool[] Modules)? Encode(string code)
    {
        code = code.Trim();
        if (code.Length == 0 || IsPlu(code)) return null;

        var digits = code.All(char.IsAsciiDigit);
        try
        {
            // UPC-A is EAN-13 with a leading 0; both writers reject a wrong check digit, which falls through to Code 128.
            if (digits && code.Length == 12) return ("UPC-A", new EAN13Writer().encode("0" + code));
            if (digits && code.Length == 13) return ("EAN-13", new EAN13Writer().encode(code));
        }
        catch (ArgumentException)
        {
        }

        try
        {
            return ("Code 128", new Code128Writer().encode(code));
        }
        catch (ArgumentException)
        {
            return null; // characters Code 128 can't carry
        }
    }

    /// <summary>An SVG with the bars, the quiet zones and the code printed underneath, or null (see <see cref="Encode"/>).</summary>
    /// <param name="moduleWidth">Width of the narrowest bar in CSS pixels. Don't let CSS rescale the SVG.</param>
    public static string? Svg(string code, int moduleWidth = 2, int barHeight = 60)
    {
        if (Encode(code) is not var (_, modules)) return null;

        const int textHeight = 18;
        var width = (modules.Length + 2 * QuietZone) * moduleWidth;
        var height = barHeight + textHeight;
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture,
            $"""<svg xmlns="http://www.w3.org/2000/svg" class="barcode-svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" shape-rendering="crispEdges" role="img" aria-label="Barcode {WebUtility.HtmlEncode(code.Trim())}">""");
        svg.Append($"""<rect width="{width}" height="{height}" fill="#fff"/>""");
        for (var i = 0; i < modules.Length; i++)
        {
            if (!modules[i] || (i > 0 && modules[i - 1])) continue;
            var run = 1;
            while (i + run < modules.Length && modules[i + run]) run++;
            svg.Append($"""<rect x="{(QuietZone + i) * moduleWidth}" width="{run * moduleWidth}" height="{barHeight}" fill="#000"/>""");
        }
        svg.Append($"""<text x="{width / 2}" y="{height - 4}" text-anchor="middle" font-family="monospace" font-size="14" fill="#000">{WebUtility.HtmlEncode(code.Trim())}</text>""");
        svg.Append("</svg>");
        return svg.ToString();
    }
}
