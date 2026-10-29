using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebPos.Data;

namespace WebPos.Services;

/// <param name="PriceOverride">A per-unit price that replaces the catalog price; needs <paramref name="ApprovedById"/>.</param>
/// <param name="ApprovedById">The manager who approved the override.</param>
public record CheckoutLine(int ItemId, int Quantity, decimal? PriceOverride = null, string? ApprovedById = null);

/// <param name="CashierId">The signed-in user making the sale.</param>
public record CheckoutRequest(string CashierId, PaymentMethod PaymentMethod, decimal AmountTendered, IReadOnlyList<CheckoutLine> Lines);

public class CheckoutService(IDbContextFactory<PosDbContext> dbFactory, IOptions<StoreOptions> store, TimeProvider time)
{
    public async Task<Item?> FindByBarcodeAsync(string barcode, CancellationToken ct = default)
    {
        barcode = barcode.Trim();
        if (barcode.Length == 0) return null;

        // Many scanners send a UPC-A code as 13-digit EAN with a leading 0 (or the reverse), so try both forms.
        var alternative = barcode.All(char.IsAsciiDigit) switch
        {
            true when barcode.Length == 13 && barcode[0] == '0' => barcode[1..],
            true when barcode.Length == 12 => "0" + barcode,
            _ => barcode,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var matches = await db.Items.AsNoTracking()
            .Where(i => (i.Barcode == barcode || i.Barcode == alternative) && i.IsActive)
            .ToListAsync(ct);
        return matches.FirstOrDefault(i => i.Barcode == barcode) ?? matches.FirstOrDefault();
    }

    /// <summary>
    /// Records a sale. Prices are read from the database and snapshotted onto each line;
    /// totals sent by the client are never trusted, and price overrides only count when a manager approved them.
    /// </summary>
    public async Task<Transaction> CompleteSaleAsync(CheckoutRequest request, CancellationToken ct = default)
    {
        var lines = request.Lines
            .GroupBy(l => (l.ItemId, l.PriceOverride, l.ApprovedById))
            .Select(g => g.First() with { Quantity = g.Sum(l => l.Quantity) })
            .ToList();

        if (lines.Count == 0) throw new CheckoutException("The cart is empty.");
        if (lines.Any(l => l.Quantity <= 0)) throw new CheckoutException("Quantities must be at least 1.");
        if (lines.Any(l => l.PriceOverride < 0)) throw new CheckoutException("Prices can't be negative.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var cashier = await db.ActiveUsersInRoles(Roles.All).FirstOrDefaultAsync(u => u.Id == request.CashierId, ct)
            ?? throw new CheckoutException("Your account can't make sales. It may have been deactivated.");
        if (!await db.Shifts.AnyAsync(s => s.UserId == cashier.Id && s.ClockOut == null, ct))
            throw new CheckoutException("Clock in before making sales.");

        var approverIds = lines.Where(l => l.PriceOverride is not null).Select(l => l.ApprovedById).OfType<string>().Distinct().ToList();
        var approvers = await db.ActiveUsersInRoles(Roles.Managers).Where(u => approverIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var ids = lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

        var transaction = new Transaction
        {
            CreatedAt = time.GetUtcNow().UtcDateTime,
            CashierId = cashier.Id,
            CashierName = cashier.DisplayName,
            PaymentMethod = request.PaymentMethod,
        };

        foreach (var line in lines)
        {
            if (!items.TryGetValue(line.ItemId, out var item) || !item.IsActive)
                throw new CheckoutException($"Item #{line.ItemId} is no longer available.");

            string? approvedBy = null;
            if (line.PriceOverride is not null
                && (line.ApprovedById is null || !approvers.TryGetValue(line.ApprovedById, out approvedBy)))
                throw new CheckoutException($"The price change on {item.Name} needs a manager's approval.");

            var unitPrice = line.PriceOverride ?? item.Price;
            transaction.Items.Add(new TransactionItem
            {
                ItemId = item.Id,
                Name = item.Name,
                Barcode = item.Barcode,
                UnitPrice = unitPrice,
                CatalogPrice = line.PriceOverride is null ? null : item.Price,
                PriceOverriddenBy = approvedBy,
                Quantity = line.Quantity,
                LineTotal = SaleCalculator.LineTotal(unitPrice, line.Quantity),
            });
        }

        var totals = SaleCalculator.Totals(transaction.Items.Select(i => (i.UnitPrice, i.Quantity)), store.Value.TaxRate);
        (transaction.Subtotal, transaction.Tax, transaction.Total) = (totals.Subtotal, totals.Tax, totals.Total);
        (transaction.AmountTendered, transaction.ChangeDue) =
            SaleCalculator.Settle(request.PaymentMethod, totals.Total, request.AmountTendered);

        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(ct);

        return transaction;
    }

    /// <param name="approvedById">The manager voiding the sale or approving the void.</param>
    public async Task VoidAsync(int transactionId, string approvedById, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var approver = await db.ActiveUsersInRoles(Roles.Managers).FirstOrDefaultAsync(u => u.Id == approvedById, ct)
            ?? throw new CheckoutException("Voiding a sale needs a manager's approval.");
        var transaction = await db.Transactions.FirstOrDefaultAsync(t => t.Id == transactionId, ct)
            ?? throw new CheckoutException($"Transaction #{transactionId} not found.");
        if (transaction.Status == TransactionStatus.Voided) return;

        transaction.Status = TransactionStatus.Voided;
        transaction.VoidedAt = time.GetUtcNow().UtcDateTime;
        transaction.VoidedBy = approver.DisplayName;
        await db.SaveChangesAsync(ct);
    }
}
