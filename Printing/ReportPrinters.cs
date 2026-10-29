using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace WebPos.Printing;

public interface IReportPrinter
{
    /// <summary>Human-readable target, e.g. "RawTcp 192.168.1.50:9100".</summary>
    string Description { get; }

    Task PrintAsync(string jobName, string text, CancellationToken ct = default);
}

public abstract class ReportPrinterBase(PrinterOptions options) : IReportPrinter
{
    protected PrinterOptions Options { get; } = options;

    public abstract string Description { get; }

    public Task PrintAsync(string jobName, string text, CancellationToken ct = default) =>
        SendAsync(jobName, Encode(text, Options), ct);

    protected abstract Task SendAsync(string jobName, byte[] data, CancellationToken ct);

    /// <summary>Printer init sequence followed by the report as 7-bit ASCII.</summary>
    public static byte[] Encode(string text, PrinterOptions options)
    {
        var init = string.IsNullOrWhiteSpace(options.InitSequence)
            ? []
            : Convert.FromHexString(options.InitSequence.Replace(" ", ""));
        using var ms = new MemoryStream();
        ms.Write(init);
        if (options.Condensed) ms.WriteByte(0x0F);
        ms.Write(Encoding.ASCII.GetBytes(text));
        return ms.ToArray();
    }

    public static IReportPrinter Create(IOptions<PrinterOptions> options) => options.Value.Transport switch
    {
        PrinterTransport.File => new FileReportPrinter(options.Value),
        PrinterTransport.Device => new DeviceReportPrinter(options.Value),
        PrinterTransport.RawTcp => new RawTcpReportPrinter(options.Value),
        PrinterTransport.Cups => new CupsReportPrinter(options.Value),
        _ => throw new InvalidOperationException($"Unknown printer transport '{options.Value.Transport}'."),
    };
}

public class FileReportPrinter(PrinterOptions options) : ReportPrinterBase(options)
{
    public override string Description => $"File: {System.IO.Path.GetFullPath(Options.Path)}";

    protected override async Task SendAsync(string jobName, byte[] data, CancellationToken ct)
    {
        Directory.CreateDirectory(Options.Path);
        var safeName = string.Concat(jobName.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        var file = System.IO.Path.Combine(Options.Path, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeName}.prn");
        await File.WriteAllBytesAsync(file, data, ct);
    }
}

public class DeviceReportPrinter(PrinterOptions options) : ReportPrinterBase(options)
{
    public override string Description => $"Device: {Options.Path}";

    protected override async Task SendAsync(string jobName, byte[] data, CancellationToken ct)
    {
        await using var stream = new FileStream(Options.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }
}

public class RawTcpReportPrinter(PrinterOptions options) : ReportPrinterBase(options)
{
    public override string Description => $"RawTcp: {Options.Host}:{Options.Port}";

    protected override async Task SendAsync(string jobName, byte[] data, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Options.Host))
            throw new InvalidOperationException("Printer:Host is not configured.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Options.TimeoutSeconds));

        using var client = new TcpClient();
        await client.ConnectAsync(Options.Host, Options.Port, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(data, timeout.Token);
        await stream.FlushAsync(timeout.Token);
        client.Client.Shutdown(SocketShutdown.Send);
    }
}

public class CupsReportPrinter(PrinterOptions options) : ReportPrinterBase(options)
{
    public override string Description => $"CUPS queue: {Options.Queue}";

    protected override async Task SendAsync(string jobName, byte[] data, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Options.Queue))
            throw new InvalidOperationException("Printer:Queue is not configured.");

        var psi = new ProcessStartInfo("lp")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-d", Options.Queue, "-o", "raw", "-t", jobName }) psi.ArgumentList.Add(arg);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Options.TimeoutSeconds));

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start 'lp'.");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.StandardInput.BaseStream.WriteAsync(data, timeout.Token);
        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);
        await stdout;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"lp exited with code {process.ExitCode}: {(await stderr).Trim()}");
    }
}
