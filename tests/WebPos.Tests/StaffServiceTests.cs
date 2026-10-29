using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebPos.Data;
using WebPos.Services;

namespace WebPos.Tests;

[Collection(PostgresCollection.Name)]
public sealed class StaffServiceTests : IAsyncDisposable
{
    private readonly PostgresFixture _db;
    private readonly ServiceProvider _services;

    public StaffServiceTests(PostgresFixture db)
    {
        _db = db;
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<PosDbContext>>(db);
        services.AddScoped(_ => db.CreateDbContext());
        services.AddSingleton(TimeProvider.System);
        services.AddPosIdentity();
        services.AddScoped<StaffService>();
        _services = services.BuildServiceProvider();
        StaffService.FailureDelay = TimeSpan.Zero;
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private StaffService Staff => _services.GetRequiredService<StaffService>();

    private async Task<StaffMember> FindAsync(string id) => (await Staff.ListAsync()).Single(m => m.Id == id);

    private static UserForm Edit(StaffMember m) => new()
    {
        Id = m.Id, UserName = m.UserName, DisplayName = m.DisplayName, Role = m.Role!, IsActive = m.IsActive, HasCard = m.HasCard,
    };

    private async Task<string> CreateAsync(string role, string password = "password123", string card = "")
    {
        var userName = "staff-" + Guid.NewGuid().ToString("N")[..8];
        var errors = await Staff.SaveAsync(new UserForm
        {
            UserName = userName, DisplayName = "Staff " + userName[6..], Role = role, Password = password, CardCode = card,
        }, actingUserId: "someone-else");
        Assert.Empty(errors);
        await using var ctx = _db.CreateDbContext();
        return await ctx.Users.Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync();
    }

    [Fact]
    public async Task There_is_always_an_active_superadmin_and_nobody_demotes_themselves()
    {
        // Leave only one active superadmin in the shared test database for a while (the other tests run after this one).
        await using var ctx = _db.CreateDbContext();
        var others = await ctx.ActiveUsersInRoles([Roles.SuperAdmin]).Select(u => u.Id).ToListAsync();
        await ctx.Users.Where(u => others.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        try
        {
            await OnlySuperadminAsync();
        }
        finally
        {
            await ctx.Users.Where(u => others.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, true));
        }
    }

    private async Task OnlySuperadminAsync()
    {
        var admin = await CreateAsync(Roles.SuperAdmin);
        var manager = await CreateAsync(Roles.Manager);

        var demote = Edit(await FindAsync(admin));
        demote.Role = Roles.Manager;
        Assert.Contains("There must always be at least one active superadmin.", await Staff.SaveAsync(demote, manager));
        var deactivate = Edit(await FindAsync(admin));
        deactivate.IsActive = false;
        Assert.Contains("There must always be at least one active superadmin.", await Staff.SaveAsync(deactivate, manager));
        Assert.Contains("You can't change your own role or deactivate your own account.", await Staff.SaveAsync(demote, admin));

        // With a second superadmin, the first can be demoted; the role is replaced, not added.
        var second = await CreateAsync(Roles.SuperAdmin);
        Assert.Empty(await Staff.SaveAsync(demote, second));
        await using var scope = _services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PosUser>>();
        Assert.Equal([Roles.Manager], await users.GetRolesAsync((await users.FindByIdAsync(admin))!));
    }

    [Fact]
    public async Task Id_cards_are_unique_hashed_and_can_be_removed()
    {
        var card = TestUsers.NewCardCode();
        var first = await CreateAsync(Roles.Cashier, card: card);
        var otherName = "staff-" + Guid.NewGuid().ToString("N")[..8];
        var errors = await Staff.SaveAsync(new UserForm
        {
            UserName = otherName, DisplayName = "Other", Role = Roles.Cashier, Password = "password123", CardCode = card,
        }, "someone-else");
        Assert.Contains(errors, e => e.StartsWith("This ID card is already assigned to Staff "));
        Assert.Contains($"ID card codes must be at least {IdCard.MinLength} characters.",
            await Staff.SaveAsync(new UserForm { UserName = otherName, DisplayName = "Other", Password = "password123", CardCode = "123" }, "x"));

        await using (var ctx = _db.CreateDbContext())
        {
            var stored = await ctx.Users.SingleAsync(u => u.Id == first);
            Assert.Equal(IdCard.Hash(card), stored.CardHash);
            Assert.DoesNotContain(card, stored.CardHash);
        }

        var member = await FindAsync(first);
        Assert.True(member.HasCard);
        var remove = Edit(member);
        remove.RemoveCard = true;
        Assert.Empty(await Staff.SaveAsync(remove, "someone-else"));
        Assert.False((await FindAsync(first)).HasCard);
    }

    [Fact]
    public async Task Setting_a_new_password_replaces_the_old_one_and_unlocks_the_account()
    {
        var id = await CreateAsync(Roles.Manager, password: "first-pass1");
        for (var i = 0; i < 5; i++)
            Assert.Equal("Invalid user name or password.", (await Staff.VerifyManagerPasswordAsync((await FindAsync(id)).UserName, "wrong-pass1")).Error);
        Assert.True((await FindAsync(id)).IsLockedOut);
        Assert.Contains("locked", (await Staff.VerifyManagerPasswordAsync((await FindAsync(id)).UserName, "first-pass1")).Error);

        var reset = Edit(await FindAsync(id));
        reset.Password = "weak";
        Assert.NotEmpty(await Staff.SaveAsync(reset, "someone-else"));
        reset.Password = "second-pass2";
        Assert.Empty(await Staff.SaveAsync(reset, "someone-else"));

        var member = await FindAsync(id);
        Assert.False(member.IsLockedOut);
        Assert.True(member.MustChangePassword);
        var (approver, error) = await Staff.VerifyManagerPasswordAsync(member.UserName, "second-pass2");
        Assert.Null(error);
        Assert.Equal(id, approver!.UserId);
    }

    [Fact]
    public async Task Only_active_managers_can_approve()
    {
        var cashierCard = TestUsers.NewCardCode();
        await CreateAsync(Roles.Cashier, card: cashierCard);
        Assert.Contains("is not a manager", (await Staff.VerifyManagerCardAsync(cashierCard)).Error);

        var managerCard = TestUsers.NewCardCode();
        var manager = await CreateAsync(Roles.Manager, card: managerCard);
        Assert.Equal(manager, (await Staff.VerifyManagerCardAsync(managerCard)).Approver?.UserId);

        var deactivate = Edit(await FindAsync(manager));
        deactivate.IsActive = false;
        Assert.Empty(await Staff.SaveAsync(deactivate, "someone-else"));
        Assert.Contains("deactivated", (await Staff.VerifyManagerCardAsync(managerCard)).Error);
    }
}
