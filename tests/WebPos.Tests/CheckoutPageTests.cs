using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Radzen;
using Radzen.Blazor;
using WebPos.Components.Pages;
using WebPos.Components.Shared;
using WebPos.Data;
using WebPos.Services;

namespace WebPos.Tests;

public class CheckoutPageTests(PostgresFixture db) : PageTestBase(db)
{
    private (IRenderedComponent<IComponent> Host, IRenderedComponent<Checkout> Page) RenderCheckout(PosUser user, string role = Roles.Cashier)
    {
        SignIn(user, role);
        var (host, page) = RenderPage<Checkout>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".item-picker .rz-data-row")));
        return (host, page);
    }

    /// <summary>Types into the scan box and presses Enter. Each find-and-trigger runs on the renderer's thread, so a
    /// re-render (say, from the last scan finishing) can't swap the element out in between.</summary>
    private static void Scan(IRenderedComponent<Checkout> page, string text)
    {
        page.InvokeAsync(() => page.Find(".scan-input").Input(text)).GetAwaiter().GetResult();
        page.InvokeAsync(() => page.Find("form.scan-form").Submit()).GetAwaiter().GetResult();
    }

    private static string GrandTotal(IRenderedComponent<Checkout> page) =>
        page.FindAll(".grand-total span")[1].TextContent;

    [Fact]
    public async Task Scanning_adds_and_merges_lines_and_flags_unknown_barcodes()
    {
        var (cashier, coffee, croissant) = await SeedAsync();
        var (_, page) = RenderCheckout(cashier);

        Scan(page, coffee.Barcode!);
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".cart-table tbody tr")));

        Scan(page, $"3*{croissant.Barcode}");
        Scan(page, coffee.Barcode!);
        page.WaitForAssertion(() => Assert.Equal("$10.85", GrandTotal(page))); // 2 x 2.50 + 3 x 1.95

        var rows = page.FindAll(".cart-table tbody tr");
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.TextContent.Contains(coffee.Name) && r.QuerySelector(".qty-value")!.TextContent == "2");
        Assert.Equal("", page.Find(".scan-input").GetAttribute("value") ?? "");

        Scan(page, "no-such-barcode");
        var notifications = Services.GetRequiredService<NotificationService>();
        page.WaitForAssertion(() => Assert.Contains(notifications.Messages, m => m.Summary == "Unknown barcode"));
        Assert.Equal(2, page.FindAll(".cart-table tbody tr").Count);
    }

    [Fact]
    public async Task Paying_cash_through_the_dialog_records_the_sale_and_shows_change()
    {
        var (cashier, coffee, croissant) = await SeedAsync();
        var (host, page) = RenderCheckout(cashier);
        Scan(page, coffee.Barcode!);
        Scan(page, $"3*{croissant.Barcode}");
        page.WaitForAssertion(() => Assert.Equal("$8.35", GrandTotal(page)));

        page.FindAll("button").Single(b => b.TextContent.Contains("Pay")).Click();
        host.WaitForState(() => host.FindComponents<PaymentDialog>().Count == 1);
        var payment = host.FindComponent<PaymentDialog>();

        payment.FindAll("button").Single(b => b.TextContent.Trim() == "$10").Click();
        payment.WaitForAssertion(() => Assert.Contains("$1.65", payment.FindAll(".pay-total")[1].TextContent));
        payment.FindAll("button").Single(b => b.TextContent.Contains("Complete sale")).Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".cart-table")));
        page.WaitForAssertion(() => Assert.Contains("$1.65", page.Find(".last-sale").TextContent));
        Assert.Contains(cashier.DisplayName, page.Find(".last-sale").TextContent);
        Assert.Contains($"Cashier: {cashier.DisplayName}", page.Find(".cashier-name").TextContent);

        await using var ctx = Db.CreateDbContext();
        var sale = await ctx.Transactions.Include(t => t.Items).SingleAsync(t => t.CashierId == cashier.Id);
        Assert.Equal(8.35m, sale.Total);
        Assert.Equal(10m, sale.AmountTendered);
        Assert.Equal(1.65m, sale.ChangeDue);
        Assert.Equal(PaymentMethod.Cash, sale.PaymentMethod);
        Assert.Equal(2, sale.Items.Count);
    }

    [Fact]
    public async Task Cashier_needs_a_manager_to_remove_an_item()
    {
        var (cashier, coffee, croissant) = await SeedAsync();
        var managerCard = TestUsers.NewCardCode();
        await Db.AddUserAsync(Roles.Manager, managerCard);
        var cashierCard = TestUsers.NewCardCode();
        await Db.AddUserAsync(Roles.Cashier, cashierCard);
        var (host, page) = RenderCheckout(cashier);
        Scan(page, $"2*{coffee.Barcode}");
        Scan(page, croissant.Barcode!);
        page.WaitForAssertion(() => Assert.Equal("$6.95", GrandTotal(page)));

        AngleSharp.Dom.IElement Row(string name) => page.FindAll(".cart-table tbody tr").Single(r => r.TextContent.Contains(name));
        IRenderedComponent<ManagerApprovalDialog> OpenApproval(Action click)
        {
            click();
            host.WaitForState(() => host.FindComponents<ManagerApprovalDialog>().Count == 1);
            return host.FindComponent<ManagerApprovalDialog>();
        }
        void ScanCard(IRenderedComponent<ManagerApprovalDialog> dialog, string code)
        {
            dialog.Find(".approval-card").Change(code);
            dialog.Find("form.approval-card-form").Submit();
        }

        // "-" asks for approval; a cashier's card and an unknown card are refused.
        var approval = OpenApproval(() => Row(coffee.Name).QuerySelector(".qty button")!.Click());
        Assert.Contains($"Remove 1 x {coffee.Name}", approval.Markup);
        ScanCard(approval, cashierCard);
        approval.WaitForAssertion(() => Assert.Contains("is not a manager", approval.Find(".approval-error").TextContent));
        ScanCard(approval, "0000000000");
        approval.WaitForAssertion(() => Assert.Contains("Card not recognized", approval.Find(".approval-error").TextContent));
        Assert.Equal("$6.95", GrandTotal(page));

        ScanCard(approval, managerCard);
        host.WaitForState(() => host.FindComponents<ManagerApprovalDialog>().Count == 0);
        page.WaitForAssertion(() => Assert.Equal("$4.45", GrandTotal(page)));

        // Cancelling leaves the sale alone.
        approval = OpenApproval(() => Row(croissant.Name).QuerySelector(".actions button")!.Click());
        Button(host, "Cancel").Click();
        host.WaitForState(() => host.FindComponents<ManagerApprovalDialog>().Count == 0);
        Assert.Equal(2, page.FindAll(".cart-table tbody tr").Count);
        Assert.Equal("$4.45", GrandTotal(page));
    }

    [Fact]
    public async Task Manager_changes_a_price_without_being_asked_for_approval()
    {
        var (_, coffee, croissant) = await SeedAsync();
        var manager = await Db.AddUserAsync(Roles.Manager);
        var (host, page) = RenderCheckout(manager, Roles.Manager);
        Scan(page, $"2*{coffee.Barcode}");
        page.WaitForAssertion(() => Assert.Equal("$5.00", GrandTotal(page)));

        page.Find(".price-button").Click();
        host.WaitForState(() => host.FindComponents<PriceDialog>().Count == 1);
        var dialog = host.FindComponent<PriceDialog>();
        dialog.Find(".rz-numeric input").Change("1.50");
        dialog.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Equal("$3.00", GrandTotal(page)));
        Assert.Empty(host.FindComponents<ManagerApprovalDialog>());
        Assert.Equal("$2.50", page.Find(".was-price").TextContent);
        Assert.DoesNotContain(page.FindAll(".cart-table tbody tr .qty button"), b => b.TextContent.Contains("add")); // no "+" on an approved price

        Scan(page, coffee.Barcode!);       // a new scan starts a regular-price line
        Scan(page, croissant.Barcode!);
        page.WaitForAssertion(() => Assert.Equal("$7.45", GrandTotal(page)));
        Assert.Equal(3, page.FindAll(".cart-table tbody tr").Count);

        page.FindAll("button").Single(b => b.TextContent.Contains("Pay")).Click();
        host.WaitForState(() => host.FindComponents<PaymentDialog>().Count == 1);
        host.FindComponent<PaymentDialog>().FindAll("button").Single(b => b.TextContent.Contains("Complete sale")).Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".cart-table")));

        await using var ctx = Db.CreateDbContext();
        var sale = await ctx.Transactions.Include(t => t.Items).SingleAsync(t => t.CashierId == manager.Id);
        Assert.Equal(7.45m, sale.Total);
        var overridden = sale.Items.Single(i => i.CatalogPrice is not null);
        Assert.Equal((1.50m, 2.50m, 2, manager.DisplayName), (overridden.UnitPrice, overridden.CatalogPrice!.Value, overridden.Quantity, overridden.PriceOverriddenBy));
    }

    [Fact]
    public async Task Customer_mode_serves_a_customer_and_scores_the_sale()
    {
        var (cashier, _, _) = await SeedAsync();
        var (host, page) = RenderCheckout(cashier);
        var game = Services.GetRequiredService<PosSession>().Game;
        game.Twists = false;

        page.Find(".rz-switch").Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".basket-item")));
        Assert.Empty(page.FindAll(".item-picker"));               // no clicking items in customer mode
        var customer = game.Current!;
        Assert.Equal(customer.Basket.Count, page.FindAll(".basket-item").Count);
        Assert.Equal(customer.Basket.Count(l => !Barcodes.IsPlu(l.Barcode)), page.FindAll(".basket-item svg.barcode-svg").Count);

        var cart = Services.GetRequiredService<PosSession>().Cart;
        foreach (var line in customer.Basket)
        {
            Scan(page, $"{line.Quantity}*{line.Barcode}");
            page.WaitForAssertion(() => Assert.Equal(line.Quantity, CustomerGame.Scanned(cart, line.ItemId))); // let each scan land
        }
        page.WaitForAssertion(() => Assert.Equal(customer.Basket.Count, page.FindAll(".basket-item.done").Count));

        page.FindAll("button").Single(b => b.TextContent.Contains("Pay")).Click();
        host.WaitForState(() => host.FindComponents<PaymentDialog>().Count == 1);
        var payment = host.FindComponent<PaymentDialog>();
        if (customer.PaysWith == PaymentMethod.Card)
            payment.FindAll(".rz-selectbar .rz-button").Single(b => b.TextContent.Contains("Card")).Click();
        else
            payment.Find(".rz-numeric input").Change(customer.CashHanded!.Value.ToString("0.00"));
        payment.FindAll("button").Single(b => b.TextContent.Contains("Complete sale")).Click();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".game-result.perfect")));
        Assert.Equal((1, 1), (game.Served, game.Perfect));
        Assert.Contains("1 served", page.Find(".game-stats").TextContent.Replace("\n", " ").Replace("  ", " "));
        Assert.Contains(customer.ExpectedTotal.ToString("C"), page.Find(".last-sale .receipt-text").TextContent);

        Scan(page, "");                                             // Enter on the empty scan box: next customer
        page.WaitForAssertion(() => Assert.Equal(2, game.Current?.Number));
        Assert.Empty(page.FindAll(".game-result"));
    }

    [Fact]
    public async Task Pay_waits_until_the_cashier_clocks_in()
    {
        var (_, coffee, _) = await SeedAsync();
        var cashier = await Db.AddUserAsync(Roles.Cashier, clockedIn: false);
        var (_, page) = RenderCheckout(cashier);
        Scan(page, coffee.Barcode!);
        page.WaitForAssertion(() => Assert.Equal("$2.50", GrandTotal(page)));

        AngleSharp.Dom.IElement Pay() => page.FindAll("button").Single(b => b.TextContent.Contains("Pay"));
        Assert.True(Pay().HasAttribute("disabled"));
        page.Find(".clocked-out button").Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".clocked-out")));
        Assert.False(Pay().HasAttribute("disabled"));
        await using var ctx = Db.CreateDbContext();
        Assert.True(await ctx.Shifts.AnyAsync(s => s.UserId == cashier.Id && s.ClockOut == null));
    }

    [Fact]
    public void Payment_dialog_blocks_short_cash_and_offers_distinct_quick_amounts()
    {
        var dialog = Render<PaymentDialog>(p => p.Add(d => d.Total, 7.35m));

        var quick = dialog.FindAll("button").Select(b => b.TextContent.Trim()).Where(t => t == "Exact" || t.StartsWith('$')).ToList();
        Assert.Equal(["Exact", "$10", "$20", "$50", "$100"], quick);

        dialog.Find(".rz-numeric input").Change("5.00");
        dialog.WaitForAssertion(() => Assert.Contains("Still due", dialog.Markup));
        Assert.True(dialog.FindAll("button").Single(b => b.TextContent.Contains("Complete sale")).HasAttribute("disabled"));
    }
}
