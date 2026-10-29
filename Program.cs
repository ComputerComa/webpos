using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Radzen;
using WebPos.Components;
using WebPos.Components.Account;
using WebPos.Components.Shared;
using WebPos.Data;
using WebPos.Printing;
using WebPos.Reports;
using WebPos.Services;
using StoreOptions = WebPos.Services.StoreOptions; // Identity has a StoreOptions type too

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();

// Blazor Server keeps a DI scope per circuit, so a scoped DbContext would be shared by
// every event on the page. Components and services create a short-lived context per operation instead.
builder.Services.AddDbContextFactory<PosDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Pos")));

// ASP.NET Core Identity: cookie sign-in, users and roles stored in the same database.
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(12); // roughly a shift; renewed while in use
    options.SlidingExpiration = true;
});
builder.Services.AddPosIdentity(); // also the role-based authorization policies
builder.Services.Configure<DefaultAdminOptions>(builder.Configuration.GetSection(DefaultAdminOptions.Section));

// Wrong passwords lock the account, but a wrong ID card code can't be pinned on anyone,
// so limit how fast one machine can try codes (or passwords) at the login form.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/Account/Login")
            ? RateLimitPartition.GetFixedWindowLimiter("login:" + context.Connection.RemoteIpAddress,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) })
            : RateLimitPartition.GetNoLimiter("none")); // partition keys must differ, or the first limiter made for a key is reused
});

builder.Services.Configure<StoreOptions>(builder.Configuration.GetSection(StoreOptions.Section));
builder.Services.Configure<PrinterOptions>(builder.Configuration.GetSection(PrinterOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<StoreClock>();
builder.Services.AddSingleton(sp => ReportPrinterBase.Create(sp.GetRequiredService<IOptions<PrinterOptions>>()));
builder.Services.AddScoped<PosSession>();
builder.Services.AddScoped<CheckoutService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<ReceiptService>();
builder.Services.AddScoped<StaffService>();
builder.Services.AddScoped<ManagerApprovals>();
builder.Services.AddScoped<TimeClock>();

var app = builder.Build();

var culture = app.Services.GetRequiredService<IOptions<StoreOptions>>().Value.Culture;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(culture);
app.UseRequestLocalization(culture);

await using (var scope = app.Services.CreateAsyncScope())
{
    await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>().CreateDbContextAsync();
    await db.Database.MigrateAsync();
    await IdentitySeeder.SeedAsync(scope.ServiceProvider);
    if (app.Configuration.GetValue<bool>("Database:SeedSampleItems")) await SampleDataSeeder.SeedItemsAsync(db);
    if (app.Configuration.GetValue<bool>("Database:SeedSampleUsers")) await SampleDataSeeder.SeedUsersAsync(scope.ServiceProvider);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseRateLimiter();

app.UseAntiforgery();

app.MapStaticAssets();

// Plain-text download of a report (exactly what is sent to the printer, minus the init codes).
app.MapGet("/reports/{kind}.txt", async (ReportKind kind, DateOnly? from, DateOnly? to, ReportService reports, StoreClock clock, CancellationToken ct) =>
{
    var request = new ReportRequest(kind, from ?? clock.Today, to ?? from ?? clock.Today);
    var text = await reports.BuildAsync(request, ct);
    return Results.File(System.Text.Encoding.ASCII.GetBytes(text), "text/plain; charset=us-ascii", ReportService.FileName(request));
}).RequireAuthorization(Policies.Manage);

app.MapAccountEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Lets the integration tests start the app with WebApplicationFactory<Program>.
public partial class Program;
