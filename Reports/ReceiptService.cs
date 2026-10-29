using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebPos.Data;
using WebPos.Services;

namespace WebPos.Reports;

public record Receipt(int TransactionId, string Number, string Text);

/// <summary>Customer receipts: 40 columns of plain ASCII, like a till slip, with the receipt number as a barcode.</summary>
public class ReceiptService(IDbContextFactory<PosDbContext> dbFactory, StoreClock clock, IOptions<StoreOptions> store)
{
    public const int Width = 40;

    /// <summary>What the receipt barcode holds, e.g. R000042.</summary>
    public static string Number(int transactionId) => $"R{transactionId:000000}";

    /// <summary>Reads a scanned or typed receipt number ("R000042", "r42" or "42").</summary>
    public static int? ParseNumber(string? text)
    {
        var digits = text?.Trim().TrimStart('R', 'r');
        return int.TryParse(digits, out var id) && id > 0 ? id : null;
    }

    public async Task<Receipt?> FindAsync(int transactionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var transaction = await db.Transactions.AsNoTracking()
            .Include(t => t.Items.OrderBy(i => i.Id))
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        return transaction is null ? null : Build(transaction);
    }

    public Receipt Build(Transaction t)
    {
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');
        void Center(string line) => Line(Fit(line).PadLeft((Width + Fit(line).Length) / 2));
        void Amount(string label, decimal amount)
        {
            var value = amount.ToString("C");
            Line(Fit(label, Width - value.Length - 1).PadRight(Width - value.Length) + value);
        }
        var rule = new string('-', Width);

        Center(store.Value.Name);
        Line(rule);
        var when = clock.ToLocal(t.CreatedAt).ToString("yyyy-MM-dd HH:mm");
        Line($"Receipt {Number(t.Id)}".PadRight(Width - when.Length) + when);
        Line(Fit($"Cashier: {t.CashierName}"));
        Line(rule);

        foreach (var item in t.Items)
        {
            if (item.Quantity == 1)
            {
                Amount(item.Name, item.LineTotal);
            }
            else
            {
                Line(Fit(item.Name));
                Amount($"  {item.Quantity} @ {item.UnitPrice:C}", item.LineTotal);
            }
            if (item.CatalogPrice is { } regular)
                Line(Fit($"  Price changed, was {regular:C}"));
        }

        Line(rule);
        Amount("Subtotal", t.Subtotal);
        if (t.Tax != 0) Amount("Tax", t.Tax);
        Amount("TOTAL", t.Total);
        Amount(t.PaymentMethod.ToString(), t.AmountTendered);
        if (t.PaymentMethod == PaymentMethod.Cash) Amount("Change", t.ChangeDue);
        Line($"Items: {t.Items.Sum(i => i.Quantity)}");
        Line(rule);
        if (t.Status == TransactionStatus.Voided)
        {
            Center("*** VOIDED ***");
            Line(rule);
        }
        Center("Thank you for shopping with us!");

        return new Receipt(t.Id, Number(t.Id), text.ToString());
    }

    private static string Fit(string text, int width = Width)
    {
        text = AsciiText.Sanitize(text);
        return text.Length <= width ? text : text[..width];
    }
}
