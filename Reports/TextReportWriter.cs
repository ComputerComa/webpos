using System.Text;

namespace WebPos.Reports;

/// <param name="Width">Characters per line (136 for a wide-carriage printer at 10 cpi, 80 for 8" paper).</param>
/// <param name="LinesPerPage">Form length in lines (66 = 11" at 6 lpi). Must match the printer's form-length setting.</param>
/// <param name="BottomMargin">Blank lines left before the perforation.</param>
/// <param name="FormFeedAtEnd">Eject to the next top-of-form when the report is done.</param>
public record ReportLayout(int Width = 136, int LinesPerPage = 66, int BottomMargin = 3, bool FormFeedAtEnd = true);

public record ReportHeader(string StoreName, string Title, IReadOnlyList<string> Subtitles, DateTime PrintedAt);

public enum Align
{
    Left,
    Right
}

/// <param name="Width">Fixed width in characters, or 0 to share whatever width is left over.</param>
public record ReportColumn(string Header, int Width, Align Align = Align.Left);

/// <summary>
/// Builds plain fixed-width text for continuous-form line printers: every page gets a header
/// (store, title, page number), the active column headings repeat after each page break,
/// pages are separated with form feeds and lines end with CR LF.
/// </summary>
public sealed class TextReportWriter
{
    public const string NewLine = "\r\n";
    public const char FormFeed = '\f';

    private readonly ReportLayout _layout;
    private readonly ReportHeader _header;
    private readonly StringBuilder _out = new();
    private ReportColumn[]? _columns;
    private int[] _widths = [];
    private int _lineOnPage;
    private bool _finished;

    public TextReportWriter(ReportLayout layout, ReportHeader header)
    {
        if (layout.Width < 40) throw new ArgumentException("Report width must be at least 40 characters.", nameof(layout));
        if (layout.BottomMargin < 0) throw new ArgumentException("Bottom margin cannot be negative.", nameof(layout));

        _layout = layout;
        _header = header;
        if (PageCapacity < PageHeaderLines + 2 + 5)
            throw new ArgumentException("Page is too short for the header and body.", nameof(layout));

        StartPage();
    }

    public int Width => _layout.Width;

    public int Page { get; private set; }

    /// <summary>Lines still available on the current page.</summary>
    public int RemainingLines => PageCapacity - _lineOnPage;

    private int PageCapacity => _layout.LinesPerPage - _layout.BottomMargin;

    private int PageHeaderLines => 3 + _header.Subtitles.Count;

    /// <summary>Starts a table. Its heading is printed now and repeated at the top of every following page.</summary>
    public void Columns(params ReportColumn[] columns)
    {
        _widths = ResolveWidths(columns, Width);
        _columns = columns;
        if (RemainingLines < 3) NewPage(); // NewPage prints the column heading
        else WriteColumnHeading();
    }

    /// <summary>Ends the current table so its heading is no longer repeated.</summary>
    public void EndColumns()
    {
        _columns = null;
        _widths = [];
    }

    public void Row(params string?[] cells)
    {
        if (_columns is null) throw new InvalidOperationException("Call Columns() before Row().");
        if (cells.Length != _columns.Length)
            throw new ArgumentException($"Expected {_columns.Length} cells but got {cells.Length}.", nameof(cells));

        var parts = new string[cells.Length];
        for (var i = 0; i < cells.Length; i++)
            parts[i] = Fit(AsciiText.Sanitize(cells[i]), _widths[i], _columns[i].Align);
        WriteLine(string.Join(' ', parts));
    }

    public void Text(string? text, Align align = Align.Left) => WriteLine(Fit(AsciiText.Sanitize(text), Width, align));

    /// <summary>Writes <paramref name="left"/> and <paramref name="right"/> on one line, the right part flush right.</summary>
    public void LeftRight(string? left, string? right) => WriteLine(LeftRightLine(left, right));

    public void Rule(char ch = '-') => WriteLine(new string(ch, Width));

    public void Blank() => WriteLine("");

    /// <summary>Moves to a new page if the next <paramref name="lines"/> lines would not fit on this one.</summary>
    public void KeepTogether(int lines)
    {
        var freshPageCapacity = PageCapacity - PageHeaderLines - (_columns is null ? 0 : 2);
        if (lines > RemainingLines && lines <= freshPageCapacity) NewPage();
    }

    public string Finish()
    {
        if (!_finished)
        {
            if (_layout.FormFeedAtEnd) _out.Append(FormFeed);
            _finished = true;
        }
        return _out.ToString();
    }

    private void StartPage()
    {
        Page++;
        _lineOnPage = 0;
        WriteRaw(LeftRightLine(_header.StoreName, $"Page {Page}"));
        WriteRaw(LeftRightLine(_header.Title, $"Printed {_header.PrintedAt:yyyy-MM-dd HH:mm}"));
        foreach (var subtitle in _header.Subtitles)
            WriteRaw(Fit(AsciiText.Sanitize(subtitle), Width, Align.Left));
        WriteRaw(new string('=', Width));
        if (_columns is not null) WriteColumnHeading();
    }

    private void NewPage()
    {
        _out.Append(FormFeed);
        StartPage();
    }

    private void WriteColumnHeading()
    {
        var parts = new string[_columns!.Length];
        for (var i = 0; i < parts.Length; i++)
            parts[i] = Fit(AsciiText.Sanitize(_columns[i].Header), _widths[i], _columns[i].Align);
        WriteRaw(string.Join(' ', parts).TrimEnd());
        WriteRaw(new string('-', Width));
    }

    private void WriteLine(string line)
    {
        if (_finished) throw new InvalidOperationException("The report has already been finished.");
        if (_lineOnPage >= PageCapacity) NewPage();
        WriteRaw(line);
    }

    private void WriteRaw(string line)
    {
        _out.Append(line.TrimEnd()).Append(NewLine);
        _lineOnPage++;
    }

    private string LeftRightLine(string? left, string? right)
    {
        var r = AsciiText.Sanitize(right);
        if (r.Length > Width) r = r[..Width];
        var room = Width - r.Length - 1;
        var l = room > 0 ? Fit(AsciiText.Sanitize(left), room, Align.Left) : "";
        return l + new string(' ', Math.Max(0, Width - l.Length - r.Length)) + r;
    }

    private static string Fit(string text, int width, Align align)
    {
        if (text.Length > width) text = text[..width];
        return align == Align.Right ? text.PadLeft(width) : text.PadRight(width);
    }

    private static int[] ResolveWidths(ReportColumn[] columns, int width)
    {
        if (columns.Length == 0) throw new ArgumentException("At least one column is required.", nameof(columns));

        var fixedWidth = columns.Sum(c => c.Width) + columns.Length - 1;
        var fillCount = columns.Count(c => c.Width == 0);
        var leftover = width - fixedWidth;
        if (leftover < fillCount || (fillCount == 0 && leftover < 0))
            throw new ArgumentException($"Columns need more than the {width} characters available.", nameof(columns));

        var widths = new int[columns.Length];
        var fillIndex = 0;
        for (var i = 0; i < columns.Length; i++)
        {
            if (columns[i].Width > 0) { widths[i] = columns[i].Width; continue; }
            // Split the leftover evenly; the first fill columns absorb any remainder.
            widths[i] = leftover / fillCount + (fillIndex < leftover % fillCount ? 1 : 0);
            fillIndex++;
        }
        return widths;
    }
}
