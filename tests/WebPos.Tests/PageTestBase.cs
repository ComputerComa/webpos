using System.Globalization;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Radzen;
using Radzen.Blazor;
using WebPos.Components.Shared;
using WebPos.Data;
using WebPos.Printing;
using WebPos.Reports;
using WebPos.Services;

namespace WebPos.Tests;

/// <summary>Renders the real Razor components (no browser) against the test database.</summary>
[Collection(PostgresCollection.Name)]
public abstract class PageTestBase : BunitContext
{
    protected PageTestBase(PostgresFixture db)
    {
        Db = db;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = new CultureInfo("en-US");

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddRadzenComponents();
        Services.AddSingleton<IDbContextFactory<PosDbContext>>(db);
        Services.AddSingleton(Options.Create(new StoreOptions { Name = "Test Store" }));
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<StoreClock>();
        Services.AddScoped<PosSession>();
        Services.AddScoped<CheckoutService>();
        Services.AddScoped(_ => db.CreateDbContext()); // for the Identity stores, one per scope as in the app
        Services.AddPosIdentity();
        Services.AddScoped<StaffService>();
        Services.AddScoped<ManagerApprovals>();
        Services.AddScoped<TimeClock>();
        StaffService.FailureDelay = TimeSpan.Zero;

        var printer = Options.Create(new PrinterOptions { Transport = PrinterTransport.File, Path = PrintDirectory, Width = 80 });
        Services.AddSingleton(printer);
        Services.AddSingleton(ReportPrinterBase.Create(printer));
        Services.AddScoped<ReportService>();
        Services.AddScoped<ReceiptService>();
    }

    protected PostgresFixture Db { get; }

    protected string PrintDirectory { get; } = Path.Combine(Path.GetTempPath(), "webpos-print-" + Guid.NewGuid().ToString("N"));

    protected async Task<(PosUser Cashier, Item Coffee, Item Croissant)> SeedAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var ctx = Db.CreateDbContext();
        var cashier = await ctx.AddUserAsync(Roles.Cashier);
        var coffee = new Item { Name = $"Coffee {tag}", Barcode = $"PC{tag}", Price = 2.50m };
        var croissant = new Item { Name = $"Croissant {tag}", Barcode = $"PK{tag}", Price = 1.95m };
        ctx.AddRange(coffee, croissant);
        await ctx.SaveChangesAsync();
        return (cashier, coffee, croissant);
    }

    /// <summary>Renders the next pages as <paramref name="user"/>, with the claims the real sign-in cookie carries.</summary>
    protected void SignIn(PosUser user, string role)
    {
        var auth = AddAuthorization();
        auth.SetAuthorized(user.UserName!);
        auth.SetRoles(role);
        // bUnit's stand-in authorization service grants exactly these policies (see IdentitySetup for the real ones).
        auth.SetPolicies(role switch
        {
            Roles.SuperAdmin => [Policies.Sell, Policies.Manage, Policies.Admin],
            Roles.Manager => [Policies.Sell, Policies.Manage],
            _ => [Policies.Sell],
        });
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(PosClaims.DisplayNameType, user.DisplayName));
    }

    /// <summary>Renders <typeparamref name="T"/> next to RadzenComponents, which hosts dialogs and notifications as in MainLayout.</summary>
    protected (IRenderedComponent<IComponent> Host, IRenderedComponent<T> Page) RenderPage<T>() where T : IComponent
    {
        var host = Render(builder =>
        {
            builder.OpenComponent<RadzenComponents>(0);
            builder.CloseComponent();
            builder.OpenComponent<T>(1);
            builder.CloseComponent();
        });
        return (host, host.FindComponent<T>());
    }

    protected static AngleSharp.Dom.IElement Button(IRenderedComponent<IComponent> host, string text) =>
        host.FindAll("button").Last(b => b.TextContent.Trim().EndsWith(text));

    protected override void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(PrintDirectory)) Directory.Delete(PrintDirectory, recursive: true);
        base.Dispose(disposing);
    }
}
