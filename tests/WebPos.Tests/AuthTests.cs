using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebPos.Data;
using WebPos.Services;

namespace WebPos.Tests;

/// <summary>Runs the whole app in memory against the test database and drives the login flow over HTTP.</summary>
[Collection(PostgresCollection.Name)]
public partial class AuthTests(PostgresFixture db) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Pos", db.ConnectionString));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    [GeneratedRegex("""<form\b.*?</form>""", RegexOptions.Singleline)]
    private static partial Regex Form();

    [GeneratedRegex("""<input[^>]*type="hidden"[^>]*>""")]
    private static partial Regex HiddenInput();

    [GeneratedRegex("""\b(name|value)="([^"]*)""")]
    private static partial Regex Attribute();

    /// <summary>GETs a page and posts the form named <paramref name="formName"/> back with its hidden fields
    /// (form name, antiforgery token) plus <paramref name="fields"/>.</summary>
    private static async Task<HttpResponseMessage> SubmitFormAsync(HttpClient client, string url, string formName, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(url);
        var formHtml = Form().Matches(html).Select(m => m.Value).Single(f => f.Contains($"value=\"{formName}\""));
        var form = new Dictionary<string, string>();
        foreach (Match input in HiddenInput().Matches(formHtml))
        {
            var attrs = Attribute().Matches(input.Value).ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));
            if (attrs.TryGetValue("name", out var name)) form[name] = attrs.GetValueOrDefault("value", "");
        }
        foreach (var (key, value) in fields) form[key] = value;
        return await client.PostAsync(url, new FormUrlEncodedContent(form));
    }

    private static string RedirectPath(HttpResponseMessage response)
    {
        var location = response.Headers.Location!;
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName, string password, string returnUrl = "/") =>
        SubmitFormAsync(client, $"/Account/Login?ReturnUrl={Uri.EscapeDataString(returnUrl)}", "login",
            new() { ["Input.UserName"] = userName, ["Input.Password"] = password });

    private static Task<HttpResponseMessage> CardLoginAsync(HttpClient client, string code) =>
        SubmitFormAsync(client, "/Account/Login?ReturnUrl=%2F", "card", new() { ["Card.Code"] = code });

    private async Task<PosUser> CreateUserAsync(string role, string password, string? cardCode = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PosUser>>();
        var user = new PosUser
        {
            UserName = $"{role.ToLowerInvariant()}-{Guid.NewGuid().ToString("N")[..8]}",
            DisplayName = $"Test {role}",
            CardHash = cardCode is null ? null : IdCard.Hash(cardCode),
        };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    private async Task DeactivateAsync(PosUser user)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PosUser>>();
        var stored = (await users.FindByIdAsync(user.Id))!;
        stored.IsActive = false;
        Assert.True((await users.UpdateAsync(stored)).Succeeded);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/items")]
    [InlineData("/reports")]
    [InlineData("/reports/PriceList.txt")]
    [InlineData("/users")]
    [InlineData("/Account/ChangePassword")]
    public async Task Anonymous_requests_are_redirected_to_login(string url)
    {
        var response = await NewClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/Account/Login?ReturnUrl={Uri.EscapeDataString(url)}", RedirectPath(response));
    }

    [Fact]
    public async Task Default_superadmin_is_seeded_and_can_log_in()
    {
        using var client = NewClient();
        Assert.Contains("Log in", await client.GetStringAsync("/Account/Login"));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<PosUser>>();
            var admin = await users.FindByNameAsync("superadmin");
            Assert.NotNull(admin);
            Assert.True(admin.MustChangePassword);
            Assert.True(await users.IsInRoleAsync(admin, Roles.SuperAdmin));
            Assert.True(await users.CheckPasswordAsync(admin, "superadmin12345"));
        }

        var wrong = await LoginAsync(client, "superadmin", "not-the-password");
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("Invalid user name or password.", await wrong.Content.ReadAsStringAsync());

        var ok = await LoginAsync(client, "superadmin", "superadmin12345", "/reports");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("/Account/ChangePassword?returnUrl=%2Freports", RedirectPath(ok)); // default password -> nudge

        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var html = await home.Content.ReadAsStringAsync();
        Assert.Contains("Superadmin", html); // display name in the header
        Assert.Contains("You are still using the default password.", html);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/reports/PriceList.txt")).StatusCode);

        // Seeding again (every startup) must not add another admin.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await IdentitySeeder.SeedAsync(scope.ServiceProvider);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<PosUser>>();
            Assert.Equal(1, await users.Users.CountAsync(u => u.UserName == "superadmin"));
        }
    }

    [Fact]
    public async Task User_can_change_password_and_log_out()
    {
        var userName = "tester-" + Guid.NewGuid().ToString("N")[..8];
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<PosUser>>();
            var user = new PosUser { UserName = userName, DisplayName = "Tester", MustChangePassword = true };
            Assert.True((await users.CreateAsync(user, "initial123")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, Roles.Cashier)).Succeeded);
        }

        using var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, userName, "initial123")).StatusCode);

        var tooWeak = await SubmitFormAsync(client, "/Account/ChangePassword", "change-password", new()
        {
            ["Input.CurrentPassword"] = "initial123", ["Input.NewPassword"] = "short", ["Input.ConfirmPassword"] = "short",
        });
        Assert.Contains("Passwords must be at least 8 characters.", await tooWeak.Content.ReadAsStringAsync());

        var changed = await SubmitFormAsync(client, "/Account/ChangePassword", "change-password", new()
        {
            ["Input.CurrentPassword"] = "initial123", ["Input.NewPassword"] = "better456", ["Input.ConfirmPassword"] = "better456",
        });
        Assert.Contains("Your password has been changed.", await changed.Content.ReadAsStringAsync());

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<PosUser>>();
            var user = (await users.FindByNameAsync(userName))!;
            Assert.False(user.MustChangePassword);
            Assert.True(await users.CheckPasswordAsync(user, "better456"));
        }

        // Still signed in after the change (the cookie was refreshed), and no default-password banner.
        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var html = await home.Content.ReadAsStringAsync();
        Assert.DoesNotContain("default password", html);

        // Logout needs the antiforgery token from the layout's logout form.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Account/Logout", new FormUrlEncodedContent([]))).StatusCode);
        var token = Regex.Match(html, """name="__RequestVerificationToken" value="([^"]+)""").Groups[1].Value;
        var logout = await client.PostAsync("/Account/Logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token) }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/Account/Login", RedirectPath(logout));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, userName, "better456")).StatusCode);
    }

    [Theory]
    [InlineData(Roles.Cashier, new[] { "/", "/transactions" }, new[] { "/reports", "/items", "/users", "/reports/PriceList.txt" })]
    [InlineData(Roles.Manager, new[] { "/", "/transactions", "/reports", "/items", "/reports/PriceList.txt" }, new[] { "/users" })]
    [InlineData(Roles.SuperAdmin, new[] { "/", "/transactions", "/reports", "/items", "/reports/PriceList.txt", "/users" }, new string[0])]
    public async Task Each_role_can_open_only_its_pages(string role, string[] allowed, string[] denied)
    {
        var user = await CreateUserAsync(role, "password123");
        using var client = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, user.UserName!, "password123")).StatusCode);

        foreach (var url in allowed)
            Assert.True((await client.GetAsync(url)).StatusCode == HttpStatusCode.OK, $"{role} should open {url}");

        foreach (var url in denied)
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal($"/Account/AccessDenied?ReturnUrl={Uri.EscapeDataString(url)}", RedirectPath(response));
        }
        Assert.Contains("Your role doesn't allow this page.", await client.GetStringAsync("/Account/AccessDenied"));

        // The menu only offers what the role can open, and the header shows who is signed in.
        var home = await client.GetStringAsync("/");
        foreach (var page in new[] { "transactions", "reports", "items", "users" })
            Assert.Equal(allowed.Contains("/" + page), home.Contains($"href=\"{page}\""));
        Assert.Contains("Test " + role, home);
    }

    [Fact]
    public async Task Users_log_in_with_an_id_card_unless_deactivated()
    {
        var card = TestUsers.NewCardCode();
        var user = await CreateUserAsync(Roles.Cashier, "password123", card);

        using var client = NewClient();
        var unknown = await CardLoginAsync(client, "0000000000");
        Assert.Contains("ID card not recognized.", await unknown.Content.ReadAsStringAsync());

        var ok = await CardLoginAsync(client, $"  {card}  "); // readers can add spaces
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("/", RedirectPath(ok));
        Assert.Contains("Test Cashier", await client.GetStringAsync("/"));

        await DeactivateAsync(user);
        using var other = NewClient();
        Assert.Contains("This account has been deactivated.", await (await CardLoginAsync(other, card)).Content.ReadAsStringAsync());
        Assert.Contains("This account has been deactivated.", await (await LoginAsync(other, user.UserName!, "password123")).Content.ReadAsStringAsync());
        // Without the right password it doesn't say whether the account exists.
        Assert.Contains("Invalid user name or password.", await (await LoginAsync(other, user.UserName!, "wrong-password1")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await other.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Login_attempts_are_rate_limited()
    {
        using var client = NewClient();
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await CardLoginAsync(client, TestUsers.NewCardCode())).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await CardLoginAsync(client, TestUsers.NewCardCode())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Account/Login")).StatusCode); // only posts are limited
    }
}
