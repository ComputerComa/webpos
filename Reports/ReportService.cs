using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebPos.Data;
using WebPos.Printing;
using WebPos.Services;

namespace WebPos.Reports;

public enum ReportKind
{
    SalesSummary,
    ItemSales,
    TransactionJournal,
    PriceList,
    TimeCards
}

public record ReportRequest(ReportKind Kind, DateOnly From, DateOnly To);

/// <summary>Builds the printable reports as fixed-width text.</summary>
public class ReportService(
    IDbContextFactory<PosDbContext> dbFactory,
    StoreClock clock,
    IOptions<StoreOptions> store,
    IOptions<PrinterOptions> printer)
{
    private const string Money = "0.00";

    public static string Title(ReportKind kind) => kind switch
    {
        ReportKind.SalesSummary => "Sales summary",
        ReportKind.ItemSales => "Item sales",
        ReportKind.TransactionJournal => "Transaction journal",
        ReportKind.PriceList => "Price list",
        ReportKind.TimeCards => "Time cards",
        _ => kind.ToString(),
    };

    public static bool UsesDateRange(ReportKind kind) => kind != ReportKind.PriceList;

    public static string FileName(ReportRequest request)
    {
        var name = Title(request.Kind).ToLowerInvariant().Replace(' ', '-');
        return UsesDateRange(request.Kind)
            ? $"{name}_{request.From:yyyy-MM-dd}_{request.To:yyyy-MM-dd}.txt"
            : $"{name}_{DateTime.Now:yyyy-MM-dd}.txt";
    }

    public async Task<string> BuildAsync(ReportRequest request, CancellationToken ct = default)
    {
        var (from, to) = request.From <= request.To ? (request.From, request.To) : (request.To, request.From);
        var subtitles = UsesDateRange(request.Kind)
            ? new[] { from == to ? $"Date: {from:yyyy-MM-dd}" : $"Period: {from:yyyy-MM-dd} to {to:yyyy-MM-dd}" }
            : ["All active items"];
        var header = new ReportHeader(store.Value.Name, Title(request.Kind).ToUpperInvariant(), subtitles, clock.Now);
        var w = new TextReportWriter(printer.Value.Layout, header);
        var (startUtc, endUtc) = clock.ToUtcRange(from, to);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        switch (request.Kind)
        {
            case ReportKind.SalesSummary: await SalesSummaryAsync(db, w, startUtc, endUtc, from != to, ct); break;
            case ReportKind.ItemSales: await ItemSalesAsync(db, w, startUtc, endUtc, ct); break;
            case ReportKind.TransactionJournal: await JournalAsync(db, w, startUtc, endUtc, ct); break;
            case ReportKind.PriceList: await PriceListAsync(db, w, ct); break;
            case ReportKind.TimeCards: await TimeCardsAsync(db, w, startUtc, endUtc, ct); break;
            default: throw new ArgumentOutOfRangeException(nameof(request));
        }

        w.Blank();
        w.Text("*** END OF REPORT ***", Align.Left);
        return w.Finish();
    }

    private async Task SalesSummaryAsync(PosDbContext db, TextReportWriter w, DateTime startUtc, DateTime endUtc, bool multiDay, CancellationToken ct)
    {
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.CreatedAt >= startUtc && t.CreatedAt < endUtc)
            .Select(t => new
            {
                t.CreatedAt, t.Status, t.PaymentMethod, t.Subtotal, t.Tax, t.Total,
                Cashier = t.CashierName,
                Items = t.Items.Sum(i => i.Quantity),
            })
            .ToListAsync(ct);

        var sales = rows.Where(r => r.Status == TransactionStatus.Completed).ToList();
        var voided = rows.Where(r => r.Status == TransactionStatus.Voided).ToList();
        var total = sales.Sum(s => s.Total);

        void Line(string label, string value) => w.Text($"{label,-32}{value,16}");

        w.Text("TOTALS (completed sales)");
        w.Blank();
        Line("Transactions", sales.Count.ToString());
        Line("Items sold", sales.Sum(s => s.Items).ToString());
        Line("Subtotal", sales.Sum(s => s.Subtotal).ToString(Money));
        Line("Tax", sales.Sum(s => s.Tax).ToString(Money));
        Line("Total", total.ToString(Money));
        Line("Average sale", (sales.Count == 0 ? 0 : SaleCalculator.RoundMoney(total / sales.Count)).ToString(Money));
        Line("Voided transactions", voided.Count.ToString());
        Line("Voided amount", voided.Sum(v => v.Total).ToString(Money));

        void Breakdown(string title, string keyHeader, IEnumerable<(string Key, int Count, decimal Total)> groups)
        {
            var list = groups.ToList();
            w.Blank();
            w.KeepTogether(Math.Min(list.Count, 5) + 4);
            w.Text(title);
            w.Columns(new(keyHeader, 30), new("Count", 8, Align.Right), new("Total", 14, Align.Right), new("Share", 8, Align.Right));
            foreach (var g in list)
                w.Row(g.Key, g.Count.ToString(), g.Total.ToString(Money), total == 0 ? "" : (g.Total / total).ToString("0.0%"));
            w.EndColumns();
        }

        Breakdown("BY PAYMENT METHOD", "Method", sales
            .GroupBy(s => s.PaymentMethod).OrderBy(g => g.Key)
            .Select(g => (g.Key.ToString(), g.Count(), g.Sum(s => s.Total))));

        Breakdown("BY CASHIER", "Cashier", sales
            .GroupBy(s => s.Cashier).OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Count(), g.Sum(s => s.Total))));

        if (multiDay)
        {
            Breakdown("BY DAY", "Date", sales
                .GroupBy(s => DateOnly.FromDateTime(clock.ToLocal(s.CreatedAt))).OrderBy(g => g.Key)
                .Select(g => (g.Key.ToString("yyyy-MM-dd ddd"), g.Count(), g.Sum(s => s.Total))));
        }
        else
        {
            Breakdown("BY HOUR", "Hour", sales
                .GroupBy(s => clock.ToLocal(s.CreatedAt).Hour).OrderBy(g => g.Key)
                .Select(g => ($"{g.Key:00}:00 - {g.Key:00}:59", g.Count(), g.Sum(s => s.Total))));
        }
    }

    private static async Task ItemSalesAsync(PosDbContext db, TextReportWriter w, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var rows = await db.TransactionItems.AsNoTracking()
            .Where(i => i.Transaction.Status == TransactionStatus.Completed
                        && i.Transaction.CreatedAt >= startUtc && i.Transaction.CreatedAt < endUtc)
            .GroupBy(i => new { i.ItemId, i.Name, i.Barcode, Category = i.Item != null ? i.Item.Category : null })
            .Select(g => new
            {
                g.Key.Name, g.Key.Barcode, g.Key.Category,
                Quantity = g.Sum(i => i.Quantity),
                Revenue = g.Sum(i => i.LineTotal),
            })
            .OrderByDescending(r => r.Revenue).ThenBy(r => r.Name)
            .ToListAsync(ct);

        w.Columns(
            new("Item", 0), new("Category", 14), new("Barcode", 14),
            new("Qty", 8, Align.Right), new("Revenue", 12, Align.Right));
        foreach (var r in rows)
            w.Row(r.Name, r.Category, r.Barcode, r.Quantity.ToString(), r.Revenue.ToString(Money));
        w.Rule();
        w.Row($"{rows.Count} items", "", "", rows.Sum(r => r.Quantity).ToString(), rows.Sum(r => r.Revenue).ToString(Money));
        w.EndColumns();
        w.Text("Revenue is before tax and excludes voided transactions.");
    }

    private async Task JournalAsync(PosDbContext db, TextReportWriter w, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var transactions = await db.Transactions.AsNoTracking()
            .Include(t => t.Items.OrderBy(i => i.Id))
            .Where(t => t.CreatedAt >= startUtc && t.CreatedAt < endUtc)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);

        w.Columns(new("", 4), new("Qty", 6, Align.Right), new("Item", 0), new("Price", 12, Align.Right), new("Amount", 12, Align.Right));
        foreach (var t in transactions)
        {
            var isVoided = t.Status == TransactionStatus.Voided;
            w.KeepTogether(t.Items.Count + t.Items.Count(i => i.CatalogPrice is not null) + (isVoided ? 5 : 4));
            w.Text($"#{t.Id:000000}  {clock.ToLocal(t.CreatedAt):yyyy-MM-dd HH:mm:ss}  {t.CashierName}  {t.PaymentMethod}{(isVoided ? "   *** VOIDED ***" : "")}");
            if (isVoided)
                w.Text($"        Voided {(t.VoidedAt is { } at ? clock.ToLocal(at).ToString("yyyy-MM-dd HH:mm") : "")} by {t.VoidedBy}");
            foreach (var i in t.Items)
            {
                w.Row("", i.Quantity.ToString(), i.Name, i.UnitPrice.ToString(Money), i.LineTotal.ToString(Money));
                if (i.CatalogPrice is { } regular)
                    w.Text($"{"",12}Price override: regular {regular.ToString(Money)}, approved by {i.PriceOverriddenBy}");
            }
            var totals = $"Subtotal {t.Subtotal.ToString(Money)}   Tax {t.Tax.ToString(Money)}   Total {t.Total.ToString(Money)}";
            var payment = $"Tendered {t.AmountTendered.ToString(Money)}   Change {t.ChangeDue.ToString(Money)}";
            if (totals.Length + payment.Length + 3 <= w.Width)
            {
                w.Text($"{totals}   {payment}", Align.Right);
            }
            else
            {
                // Narrow paper: two lines rather than clipping the change amount.
                w.Text(totals, Align.Right);
                w.Text(payment, Align.Right);
            }
            w.Blank();
        }
        w.EndColumns();

        var completed = transactions.Where(t => t.Status == TransactionStatus.Completed).ToList();
        w.Rule('=');
        w.Text($"{completed.Count} completed, {transactions.Count - completed.Count} voided.   " +
               $"Total of completed sales: {completed.Sum(t => t.Total).ToString(Money)}");
    }

    private static async Task PriceListAsync(PosDbContext db, TextReportWriter w, CancellationToken ct)
    {
        var items = await db.Items.AsNoTracking()
            .Where(i => i.IsActive)
            .OrderBy(i => i.Category).ThenBy(i => i.Name)
            .ToListAsync(ct);

        w.Columns(new("Item", 0), new("Category", 16), new("Barcode", 14), new("Price", 12, Align.Right));
        foreach (var i in items)
            w.Row(i.Name, i.Category, i.Barcode, i.Price.ToString(Money));
        w.EndColumns();
        w.Rule();
        w.Text($"{items.Count} active items");
    }

    /// <summary>Shifts that started in the period, by employee. Open shifts count up to now.</summary>
    private async Task TimeCardsAsync(PosDbContext db, TextReportWriter w, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var shifts = await db.Shifts.AsNoTracking()
            .Where(s => s.ClockIn >= startUtc && s.ClockIn < endUtc)
            .Select(s => new { s.User.DisplayName, s.ClockIn, s.ClockOut })
            .ToListAsync(ct);

        static string Hours(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}";
        var now = clock.Now;
        var total = TimeSpan.Zero;

        w.Columns(new("Employee", 0), new("Date", 16), new("In", 7), new("Out", 14), new("Hours", 8, Align.Right));
        foreach (var person in shifts.GroupBy(s => s.DisplayName).OrderBy(g => g.Key))
        {
            var list = person.OrderBy(s => s.ClockIn).ToList();
            w.KeepTogether(list.Count + 2);
            var subtotal = TimeSpan.Zero;
            foreach (var s in list)
            {
                var start = clock.ToLocal(s.ClockIn);
                var end = s.ClockOut is { } o ? clock.ToLocal(o) : now;
                subtotal += end - start;
                var outText = s.ClockOut is null ? "on the clock"
                    : end.Date == start.Date ? end.ToString("HH:mm") : end.ToString("MM-dd HH:mm");
                w.Row(person.Key, start.ToString("yyyy-MM-dd ddd"), start.ToString("HH:mm"), outText, Hours(end - start));
            }
            w.Row("", "", "", "Total", Hours(subtotal));
            w.Blank();
            total += subtotal;
        }
        w.EndColumns();
        w.Rule();
        w.Text($"{shifts.Count} shifts, {Hours(total)} hours.");
        w.Text("Shifts are listed on the day they started; open ones count up to now.");
    }
}
