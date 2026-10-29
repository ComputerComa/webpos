using WebPos.Reports;

namespace WebPos.Printing;

public enum PrinterTransport
{
    /// <summary>Writes each job as a .prn file into <see cref="PrinterOptions.Path"/> (for development).</summary>
    File,

    /// <summary>Writes raw bytes to a device node such as /dev/usb/lp0 or /dev/lp0.</summary>
    Device,

    /// <summary>Sends raw bytes to a network print server / printer NIC, usually on port 9100.</summary>
    RawTcp,

    /// <summary>Submits a raw job to a CUPS queue with <c>lp -o raw</c>.</summary>
    Cups
}

public class PrinterOptions
{
    public const string Section = "Printer";

    public string Name { get; set; } = "OKI Microline 4410";
    public PrinterTransport Transport { get; set; } = PrinterTransport.File;

    /// <summary>File: output directory. Device: device path.</summary>
    public string Path { get; set; } = "printouts";

    public string? Host { get; set; }
    public int Port { get; set; } = 9100;

    public string? Queue { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    // Page layout
    public int Width { get; set; } = 136;
    public int LinesPerPage { get; set; } = 66;
    public int BottomMargin { get; set; } = 3;
    public bool FormFeedAtEnd { get; set; } = true;

    /// <summary>
    /// Bytes sent before every job, as hex. "1B40" is ESC @ (reset) in Epson FX emulation.
    /// Leave empty for IBM Proprinter emulation, which has no equivalent reset.
    /// </summary>
    public string InitSequence { get; set; } = "1B40";

    /// <summary>Sends SI (0x0F) to switch to condensed 17 cpi. Increase <see cref="Width"/> to match.</summary>
    public bool Condensed { get; set; }

    public ReportLayout Layout => new(Width, LinesPerPage, BottomMargin, FormFeedAtEnd);
}
