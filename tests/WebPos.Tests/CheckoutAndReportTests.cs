using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using WebPos.Data;
using WebPos.Printing;
using WebPos.Reports;
using WebPos.Services;

namespace WebPos.Tests;

[Collection(PostgresCollection.Name)]
public class CheckoutAndReportTests(PostgresFixture db)
{
    private static readonly IOptions<StoreOptions> Store =
        Options.Create(new StoreOptions { Name = "Test Store", TaxRate = 0.08m, TimeZone = "America/New_York" });

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 29, 15, 0, 0, TimeSpan.Zero));

    private CheckoutService Checkout => new(db, Store, _time);

    private async Task<(PosUser Cashier, Item Coffee, Item Muffin)> SeedAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var ctx = db.CreateDbContext();
        var cashier = await ctx.AddUserAsync(Roles.Cashier);
        var coffee = new Item { Name = $"Coffee {tag}", Barcode = $"C{tag}", Price = 2.50m };
        var muffin = new Item { Name = $"Muffin {tag}", Barcode = $"M{tag}", Price = 1.95m };
        ctx.AddRange(coffee, muffin);
        await ctx.SaveChangesAsync();
        return (cashier, coffee, muffin);
    }

    [Fact]
    public async Task Sale_snapshots_prices_and_is_not_affected_by_later_price_changes()
    {
        var (cashier, coffee, muffin) = await SeedAsync();

        var sale = await Checkout.CompleteSaleAsync(new CheckoutRequest(cashier.Id, PaymentMethod.Cash, 20m,
            [new(coffee.Id, 2), new(muffin.Id, 1), new(coffee.Id, 1)]));

        // 3 x 2.50 + 1 x 1.95 = 9.45; tax 8% = 0.756 -> 0.76
        Assert.Equal(9.45m, sale.Subtotal);
        Assert.Equal(0.76m, sale.Tax);
        Assert.Equal(10.21m, sale.Total);
        Assert.Equal(9.79m, sale.ChangeDue);
        Assert.Equal(2, sale.Items.Count); // repeated item lines are merged
        Assert.Equal(cashier.Id, sale.CashierId);
        Assert.Equal(cashier.DisplayName, sale.CashierName);

        await using (var ctx = db.CreateDbContext())
        {
            var item = await ctx.Items.SingleAsync(i => i.Id == coffee.Id);
            item.Price = 9.99m;
            item.Name = "Renamed";
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = db.CreateDbContext())
        {
            var stored = await ctx.Transactions.Include(t => t.Items).SingleAsync(t => t.Id == sale.Id);
            var line = stored.Items.Single(i => i.ItemId == coffee.Id);
            Assert.Equal(2.50m, line.UnitPrice);
            Assert.Equal(coffee.Name, line.Name);
            Assert.Equal(3, line.Quantity);
            Assert.Equal(7.50m, line.LineTotal);
            Assert.Equal(10.21m, stored.Total);
        }
    }

    [Fact]
    public async Task Insufficient_cash_is_rejected_and_nothing_is_saved()
    {
        var (cashier, coffee, _) = await SeedAsync();

        await Assert.ThrowsAsync<CheckoutException>(() => Checkout.CompleteSaleAsync(
            new CheckoutRequest(cashier.Id, PaymentMethod.Cash, 1m, [new(coffee.Id, 1)])));

        await using var ctx = db.CreateDbContext();
        Assert.False(await ctx.Transactions.AnyAsync(t => t.CashierId == cashier.Id));
    }

    [Fact]
    public async Task Barcode_lookup_accepts_upc_a_with_or_without_the_leading_zero()
    {
        var tag = Random.Shared.Next(1_000_000, 9_999_999).ToString();
        var upc = new Item { Name = "UPC " + tag, Barcode = $"4{tag}0000", Price = 1m };  // 12 digits
        var ean = new Item { Name = "EAN " + tag, Barcode = $"0{tag}99990", Price = 1m }; // 13 digits, leading 0
        await using (var ctx = db.CreateDbContext())
        {
            ctx.AddRange(upc, ean);
            await ctx.SaveChangesAsync();
        }

        Assert.Equal(upc.Id, (await Checkout.FindByBarcodeAsync(upc.Barcode))?.Id);
        Assert.Equal(upc.Id, (await Checkout.FindByBarcodeAsync("0" + upc.Barcode))?.Id); // scanner sent EAN-13
        Assert.Equal(ean.Id, (await Checkout.FindByBarcodeAsync(ean.Barcode[1..]))?.Id);  // scanner sent UPC-A
        Assert.Null(await Checkout.FindByBarcodeAsync("1" + upc.Barcode));
    }

    [Fact]
    public async Task Inactive_items_and_deactivated_users_cannot_sell()
    {
        var (cashier, coffee, muffin) = await SeedAsync();
        await using (var ctx = db.CreateDbContext())
        {
            await ctx.Items.Where(i => i.Id == coffee.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsActive, false));
        }

        await Assert.ThrowsAsync<CheckoutException>(() => Checkout.CompleteSaleAsync(
            new CheckoutRequest(cashier.Id, PaymentMethod.Card, 0m, [new(coffee.Id, 1)])));
        Assert.Null(await Checkout.FindByBarcodeAsync(coffee.Barcode!));

        // The sign-in cookie can outlive a deactivation, so the service checks the database.
        var gone = await db.AddUserAsync(Roles.Manager, active: false);
        var ex = await Assert.ThrowsAsync<CheckoutException>(() => Checkout.CompleteSaleAsync(
            new CheckoutRequest(gone.Id, PaymentMethod.Card, 0m, [new(muffin.Id, 1)])));
        Assert.Contains("deactivated", ex.Message);
    }

    [Fact]
    public async Task Price_overrides_need_an_active_manager_and_are_snapshotted()
    {
        var (cashier, coffee, muffin) = await SeedAsync();
        var manager = await db.AddUserAsync(Roles.Manager);
        var otherCashier = await db.AddUserAsync(Roles.Cashier);
        var formerManager = await db.AddUserAsync(Roles.Manager, active: false);

        foreach (var approverId in new string?[] { null, otherCashier.Id, cashier.Id, formerManager.Id, "no-such-user" })
        {
            var ex = await Assert.ThrowsAsync<CheckoutException>(() => Checkout.CompleteSaleAsync(new CheckoutRequest(
                cashier.Id, PaymentMethod.Card, 0m, [new(coffee.Id, 1, 1.00m, approverId)])));
            Assert.Contains("manager's approval", ex.Message);
        }

        var sale = await Checkout.CompleteSaleAsync(new CheckoutRequest(cashier.Id, PaymentMethod.Card, 0m,
            [new(coffee.Id, 2, 1.00m, manager.Id), new(coffee.Id, 1), new(muffin.Id, 1)]));

        // 2 x 1.00 (override) + 1 x 2.50 + 1 x 1.95 = 6.45, plus 8% tax (0.516 -> 0.52)
        Assert.Equal(6.45m, sale.Subtotal);
        Assert.Equal(6.97m, sale.Total);
        var overridden = sale.Items.Single(i => i.CatalogPrice is not null);
        Assert.Equal((1.00m, 2.50m, 2, manager.DisplayName), (overridden.UnitPrice, overridden.CatalogPrice!.Value, overridden.Quantity, overridden.PriceOverriddenBy));
        var regular = sale.Items.Single(i => i.ItemId == coffee.Id && i.CatalogPrice is null);
        Assert.Equal((2.50m, 1, (string?)null), (regular.UnitPrice, regular.Quantity, regular.PriceOverriddenBy));

        await Assert.ThrowsAsync<CheckoutException>(() => Checkout.CompleteSaleAsync(new CheckoutRequest(
            cashier.Id, PaymentMethod.Card, 0m, [new(coffee.Id, 1, -1m, manager.Id)])));
    }

    [Fact]
    public async Task Receipts_are_40_column_ascii_slips_with_the_sale_details()
    {
        var (cashier, coffee, muffin) = await SeedAsync();
        var manager = await db.AddUserAsync(Roles.Manager);
        var sale = await Checkout.CompleteSaleAsync(new CheckoutRequest(cashier.Id, PaymentMethod.Cash, 20m,
            [new(coffee.Id, 3), new(muffin.Id, 1, 1.00m, manager.Id)]));
        var receipts = new ReceiptService(db, new StoreClock(Store, _time), Store);

        var receipt = (await receipts.FindAsync(sale.Id))!;
        var lines = receipt.Text.TrimEnd('\n').Split('\n');

        Assert.Equal($"R{sale.Id:000000}", receipt.Number);
        Assert.All(lines, l => Assert.True(l.Length <= ReceiptService.Width, l));
        Assert.All(receipt.Text, ch => Assert.True(ch < 128));
        Assert.Equal("Test Store", lines[0].Trim());
        Assert.Contains(lines, l => l.StartsWith($"Receipt R{sale.Id:000000} ") && l.EndsWith(" 2026-10-29 11:00")); // 15:00 UTC in New York
        Assert.Contains($"Cashier: {cashier.DisplayName}", lines);
        Assert.Contains(lines, l => l.StartsWith("  3 @ $2.50") && l.EndsWith("$7.50"));
        Assert.Contains(lines, l => l.StartsWith(muffin.Name[..Math.Min(muffin.Name.Length, 20)]) && l.EndsWith("$1.00"));
        Assert.Contains("  Price changed, was $1.95", lines);
        Assert.Contains(lines, l => l.StartsWith("TOTAL") && l.EndsWith("$9.18"));  // 8.50 + 8% tax
        Assert.Contains(lines, l => l.StartsWith("Change") && l.EndsWith("$10.82"));
        Assert.DoesNotContain("VOIDED", receipt.Text);

        await Checkout.VoidAsync(sale.Id, manager.Id);
        Assert.Contains("*** VOIDED ***", (await receipts.FindAsync(sale.Id))!.Text);
        Assert.Null(await receipts.FindAsync(int.MaxValue));
    }

    [Theory]
    [InlineData("R000042", 42)]
    [InlineData(" r42 ", 42)]
    [InlineData("42", 42)]
    [InlineData("R0", null)]
    [InlineData("coffee", null)]
    [InlineData("", null)]
    public void Receipt_numbers_parse_from_scans_or_typing(string text, int? expected)
    {
        Assert.Equal(expected, ReceiptService.ParseNumber(text));
    }

    [Fact]
    public async Task Only_an_active_manager_can_void_a_sale()
    {
        var (cashier, coffee, _) = await SeedAsync();
        var manager = await db.AddUserAsync(Roles.SuperAdmin);
        var sale = await Checkout.CompleteSaleAsync(new CheckoutRequest(cashier.Id, PaymentMethod.Card, 0m, [new(coffee.Id, 1)]));

        await Assert.ThrowsAsync<CheckoutException>(() => Checkout.VoidAsync(sale.Id, cashier.Id));
        await Checkout.VoidAsync(sale.Id, manager.Id);

        await using var ctx = db.CreateDbContext();
        var stored = await ctx.Transactions.SingleAsync(t => t.Id == sale.Id);
        Assert.Equal(TransactionStatus.Voided, stored.Status);
        Assert.Equal(manager.DisplayName, stored.VoidedBy);
    }

    [Fact]
    public async Task Reports_cover_the_store_day_and_exclude_voided_sales()
    {
        var (cashier, coffee, muffin) = await SeedAsync();
        var manager = await db.AddUserAsync(Roles.Manager);
        // 2026-10-29 23:30 in New York is 2026-10-30 03:30 UTC; it must count for the 29th.
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 30, 3, 30, 0, TimeSpan.Zero));
        var late = await Checkout.CompleteSaleAsync(new CheckoutRequest(cashier.Id, PaymentMethod.Card, 0m, [new(coffee.Id, 4)]));
        var voided = await Checkout.CompleteSaleAsync(new CheckoutRequest(cashier.Id, PaymentMethod.Cash, 50m, [new(muffin.Id, 10)]));
        await Checkout.VoidAsync(voided.Id, manager.Id);
        var discounted = await Checkout.CompleteSaleAsync(new CheckoutRequest(cashier.Id, PaymentMethod.Card, 0m, [new(coffee.Id, 1, 0.99m, manager.Id)]));

        var clock = new StoreClock(Store, _time);
        var printer = Options.Create(new PrinterOptions { Width = 80, LinesPerPage = 66 });
        var reports = new ReportService(db, clock, Store, printer);
        var day = new DateOnly(2026, 10, 29);

        var items = await reports.BuildAsync(new ReportRequest(ReportKind.ItemSales, day, day));
        Assert.Contains(coffee.Name, items);
        Assert.DoesNotContain(muffin.Name, items); // only sold in the voided transaction

        var journal = await reports.BuildAsync(new ReportRequest(ReportKind.TransactionJournal, day, day));
        Assert.Contains($"#{late.Id:000000}  2026-10-29 23:30", journal);
        Assert.Contains("*** VOIDED ***", journal);
        Assert.Contains($"Voided 2026-10-29 23:30 by {manager.DisplayName}", journal);
        Assert.Contains($"#{discounted.Id:000000}  2026-10-29 23:30:00  {cashier.DisplayName}  Card", journal);
        Assert.Contains($"Price override: regular 2.50, approved by {manager.DisplayName}", journal);

        var summary = await reports.BuildAsync(new ReportRequest(ReportKind.SalesSummary, day, day));
        Assert.Contains(cashier.DisplayName, summary); // the "by cashier" breakdown uses the name snapshot

        var nextDay = await reports.BuildAsync(new ReportRequest(ReportKind.TransactionJournal, day.AddDays(1), day.AddDays(1)));
        Assert.DoesNotContain($"#{late.Id:000000}", nextDay);

        var big = await Checkout.CompleteSaleAsync(new CheckoutRequest(cashier.Id, PaymentMethod.Cash, 99999m, [new(coffee.Id, 9999)]));
        var bigJournal = await reports.BuildAsync(new ReportRequest(ReportKind.TransactionJournal, day, day));
        Assert.Contains($"Change {big.ChangeDue:0.00}", bigJournal);

        foreach (var kind in Enum.GetValues<ReportKind>())
        {
            var text = await reports.BuildAsync(new ReportRequest(kind, day, day));
            Assert.Contains("*** END OF REPORT ***", text);
            Assert.All(text.Split("\r\n"), line => Assert.True(line.TrimStart('\f').Length <= 80, line));
            Assert.All(text, ch => Assert.True(ch < 128));
        }
    }
}
