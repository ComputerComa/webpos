using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using WebPos.Components.Pages;
using WebPos.Components.Shared;
using WebPos.Data;
using WebPos.Services;

namespace WebPos.Tests;

public class AdminPageTests(PostgresFixture db) : PageTestBase(db)
{
    [Fact]
    public async Task Transactions_page_lists_a_sale_and_a_manager_approves_the_void()
    {
        var managerCard = TestUsers.NewCardCode();
        var (cashier, coffee, _) = await SeedAsync();
        var manager = await Db.AddUserAsync(Roles.Manager, managerCard);
        SignIn(cashier, Roles.Cashier); // before anything is resolved from Services
        var sale = await Services.GetRequiredService<CheckoutService>().CompleteSaleAsync(
            new CheckoutRequest(cashier.Id, PaymentMethod.Card, 0m, [new(coffee.Id, 2)]));

        var (host, page) = RenderPage<Transactions>();
        page.WaitForAssertion(() => Assert.Contains(page.FindAll(".rz-data-row"), r => r.TextContent.Contains(cashier.DisplayName)));
        var row = page.FindAll(".rz-data-row").Single(r => r.TextContent.Contains(cashier.DisplayName));
        Assert.Contains("$5.00", row.TextContent);
        Assert.Contains("Completed", row.TextContent);

        row.QuerySelector("td")!.Click();
        host.WaitForState(() => host.FindComponents<TransactionDialog>().Count == 1);
        var dialog = host.FindComponent<TransactionDialog>();
        dialog.WaitForAssertion(() => Assert.Contains(coffee.Name, dialog.Markup));

        Button(host, "Void").Click();                                   // opens the confirmation
        host.WaitForAssertion(() => Assert.Equal(2, host.FindAll("button").Count(b => b.TextContent.Trim().EndsWith("Void"))));
        Button(host, "Void").Click();                                   // confirms
        host.WaitForState(() => host.FindComponents<ManagerApprovalDialog>().Count == 1);
        var approval = host.FindComponent<ManagerApprovalDialog>();
        approval.Find(".approval-username").Change(manager.UserName!);     // no password set: refused
        approval.Find(".approval-password").Change("whatever1");
        approval.Find("form.approval-password-form").Submit();
        approval.WaitForAssertion(() => Assert.Contains("Invalid user name or password", approval.Find(".approval-error").TextContent));
        approval.Find(".approval-card").Change(managerCard);
        approval.Find("form.approval-card-form").Submit();
        dialog.WaitForAssertion(() => Assert.Contains($"by {manager.DisplayName}", dialog.Markup));

        await using (var ctx = Db.CreateDbContext())
        {
            var stored = await ctx.Transactions.SingleAsync(t => t.Id == sale.Id);
            Assert.Equal((TransactionStatus.Voided, manager.DisplayName), (stored.Status, stored.VoidedBy));
        }

        Button(host, "Close").Click();
        page.WaitForAssertion(() => Assert.Contains("Voided",
            page.FindAll(".rz-data-row").Single(r => r.TextContent.Contains(cashier.DisplayName)).TextContent));
    }

    [Fact]
    public async Task Scanning_a_receipt_opens_its_sale_and_the_receipt_page_prints_it()
    {
        var (cashier, coffee, _) = await SeedAsync();
        SignIn(cashier, Roles.Cashier);
        var sale = await Services.GetRequiredService<CheckoutService>().CompleteSaleAsync(
            new CheckoutRequest(cashier.Id, PaymentMethod.Card, 0m, [new(coffee.Id, 1)]));
        await using (var ctx = Db.CreateDbContext())
        {
            // An older sale, outside the dates the page shows by default.
            await ctx.Transactions.Where(t => t.Id == sale.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, t => t.CreatedAt.AddDays(-40)));
        }

        var (host, page) = RenderPage<Transactions>();
        page.Find("input[name=ReceiptSearch]").Change($"R{sale.Id:000000}");
        page.Find("form.receipt-search").Submit();
        host.WaitForState(() => host.FindComponents<TransactionDialog>().Count == 1);
        var dialog = host.FindComponent<TransactionDialog>();
        dialog.WaitForAssertion(() => Assert.Contains(coffee.Name, dialog.Markup));

        Button(host, "Receipt").Click();
        host.WaitForState(() => host.FindComponents<ReceiptDialog>().Count == 1);
        var receipt = host.FindComponent<ReceiptDialog>();
        receipt.WaitForAssertion(() => Assert.Contains($"Receipt R{sale.Id:000000}", receipt.Find(".receipt-text").TextContent));
        Assert.Contains($">R{sale.Id:000000}</text>", receipt.Markup);   // the barcode

        Services.GetRequiredService<NavigationManager>().NavigateTo($"receipts/{sale.Id}?print=true");
        var print = Render<ReceiptPage>(p => p.Add(r => r.Id, sale.Id));
        print.WaitForAssertion(() => Assert.Contains(coffee.Name, print.Find(".receipt-text").TextContent));
        print.WaitForAssertion(() => JSInterop.VerifyInvoke("print"));
    }

    [Fact]
    public async Task Reports_page_previews_and_prints_to_the_configured_printer()
    {
        var (cashier, coffee, _) = await SeedAsync();
        await Services.GetRequiredService<CheckoutService>().CompleteSaleAsync(
            new CheckoutRequest(cashier.Id, PaymentMethod.Cash, 10m, [new(coffee.Id, 1)]));

        var (host, page) = RenderPage<WebPos.Components.Pages.Reports>();

        Button(host, "Preview").Click();
        page.WaitForAssertion(() => Assert.Contains("SALES SUMMARY", page.Find(".report-page").TextContent));
        Assert.Contains(cashier.DisplayName, page.Markup);
        Assert.Contains("reports/SalesSummary.txt?from=", page.Markup);

        Button(host, "Print").Click();
        page.WaitForAssertion(() => Assert.Single(Directory.GetFiles(PrintDirectory, "*.prn")));
        var notifications = Services.GetRequiredService<NotificationService>();
        page.WaitForAssertion(() => Assert.Contains(notifications.Messages, m => m.Summary == "Sent to printer"));

        var bytes = await File.ReadAllBytesAsync(Directory.GetFiles(PrintDirectory, "*.prn").Single());
        Assert.Equal([0x1B, 0x40], bytes[..2]);                       // ESC @ init
        Assert.Equal((byte)'\f', bytes[^1]);                           // ends with a form feed
    }

    [Fact]
    public async Task Users_page_adds_a_user_with_a_role_and_an_id_card()
    {
        var admin = await Db.AddUserAsync(Roles.SuperAdmin);
        SignIn(admin, Roles.SuperAdmin);
        var (host, page) = RenderPage<Users>();
        page.WaitForAssertion(() => Assert.Contains(page.FindAll(".rz-data-row"), r => r.TextContent.Contains(admin.DisplayName)));

        Button(host, "Add user").Click();
        host.WaitForState(() => host.FindComponents<UserDialog>().Count == 1);
        var dialog = host.FindComponent<UserDialog>();
        var userName = "new-" + Guid.NewGuid().ToString("N")[..8];
        var card = TestUsers.NewCardCode();
        dialog.Find("input[name=UserName]").Change(userName);
        dialog.Find("input[name=Password]").Change("short");
        dialog.Find("input[name=CardCode]").Change(card);
        dialog.Find("form.user-form").Submit();
        dialog.WaitForAssertion(() => Assert.Contains("Enter a display name.", dialog.Find(".user-errors").TextContent));

        dialog.Find("input[name=DisplayName]").Change("Nora New");
        dialog.Find("form.user-form").Submit();
        dialog.WaitForAssertion(() => Assert.Contains("at least 8 characters", dialog.Find(".user-errors").TextContent));

        dialog.Find("input[name=Password]").Change("welcome123");
        dialog.Find("form.user-form").Submit();
        host.WaitForState(() => host.FindComponents<UserDialog>().Count == 0);
        page.WaitForAssertion(() => Assert.Contains(page.FindAll(".rz-data-row"), r => r.TextContent.Contains("Nora New")));
        Assert.Contains("Cashier", page.FindAll(".rz-data-row").Single(r => r.TextContent.Contains("Nora New")).TextContent); // the default role

        await using var ctx = Db.CreateDbContext();
        var user = await ctx.Users.SingleAsync(u => u.UserName == userName);
        Assert.Equal(IdCard.Hash(card), user.CardHash);
        Assert.True(user.MustChangePassword);
        Assert.True(user.IsActive);
    }

    [Fact]
    public async Task Items_page_adds_an_item_inline()
    {
        await SeedAsync();
        var name = "Widget " + Guid.NewGuid().ToString("N")[..8];
        var (host, page) = RenderPage<Items>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".rz-data-row")));

        Button(host, "Add item").Click();
        page.WaitForElements(".rz-data-row:first-child input");
        // Every change re-renders the row, so look the inputs up again each time.
        AngleSharp.Dom.IElement Input(int index) => page.FindAll(".rz-data-row:first-child input")[index];
        Input(0).Change(name);
        Input(1).Change("  ");                                          // blank barcode is stored as null
        Input(3).Change("4.20");
        page.Find(".rz-data-row:first-child button").Click();             // save (check icon)

        await using var ctx = Db.CreateDbContext();
        page.WaitForAssertion(() => Assert.True(ctx.Items.Any(i => i.Name == name)));
        var item = await ctx.Items.AsNoTracking().SingleAsync(i => i.Name == name);
        Assert.Equal(4.20m, item.Price);
        Assert.Null(item.Barcode);
        Assert.True(item.IsActive);
    }
}
