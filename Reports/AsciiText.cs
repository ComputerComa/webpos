using System.Globalization;
using System.Text;

namespace WebPos.Reports;

/// <summary>
/// Line printers in Epson FX / IBM Proprinter emulation only reliably print 7-bit ASCII.
/// Accented letters are folded to their base letter, a few common symbols are spelled out,
/// and control characters are replaced so item names cannot inject printer escape codes.
/// </summary>
public static class AsciiText
{
    private static readonly Dictionary<char, string> Replacements = new()
    {
        ['ß'] = "ss", ['æ'] = "ae", ['Æ'] = "AE", ['ø'] = "o", ['Ø'] = "O", ['œ'] = "oe", ['Œ'] = "OE",
        ['ł'] = "l", ['Ł'] = "L", ['đ'] = "d", ['Đ'] = "D", ['þ'] = "th", ['Þ'] = "Th",
        ['‘'] = "'", ['’'] = "'", ['‚'] = "'", ['“'] = "\"", ['”'] = "\"", ['„'] = "\"",
        ['–'] = "-", ['—'] = "-", ['…'] = "...", ['•'] = "*", ['×'] = "x",
        ['€'] = "EUR", ['£'] = "GBP", ['¥'] = "JPY", ['©'] = "(c)", ['®'] = "(R)", ['™'] = "TM",
        [' '] = " ",
    };

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (ch is >= ' ' and <= '~') sb.Append(ch);
            else if (char.IsControl(ch)) sb.Append(' ');
            else if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            else if (Replacements.TryGetValue(ch, out var replacement)) sb.Append(replacement);
            else sb.Append('?');
        }
        return sb.ToString();
    }
}
