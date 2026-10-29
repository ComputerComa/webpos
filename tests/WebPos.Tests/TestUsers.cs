using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebPos.Data;
using WebPos.Services;

namespace WebPos.Tests;

public static class TestUsers
{
    /// <summary>Adds a user with <paramref name="role"/> straight to the database, without a password.
    /// They start clocked in, so they can make sales.</summary>
    public static async Task<PosUser> AddUserAsync(this PosDbContext ctx, string role, string? cardCode = null, bool active = true, bool clockedIn = true)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var roleRow = await ctx.Roles.SingleAsync(r => r.NormalizedName == role.ToUpperInvariant()); // seeded by PostgresFixture

        var userName = $"{role.ToLowerInvariant()}-{tag}";
        var user = new PosUser
        {
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            DisplayName = $"{role} {tag}",
            IsActive = active,
            SecurityStamp = Guid.NewGuid().ToString(),
            CardHash = cardCode is null ? null : IdCard.Hash(cardCode),
        };
        ctx.Users.Add(user);
        ctx.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = roleRow.Id });
        if (clockedIn) ctx.Shifts.Add(new Shift { UserId = user.Id, ClockIn = DateTime.UtcNow.AddHours(-1) });
        await ctx.SaveChangesAsync();
        return user;
    }

    public static async Task<PosUser> AddUserAsync(this IDbContextFactory<PosDbContext> db, string role, string? cardCode = null, bool active = true, bool clockedIn = true)
    {
        await using var ctx = await db.CreateDbContextAsync();
        return await ctx.AddUserAsync(role, cardCode, active, clockedIn);
    }

    public static string NewCardCode() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();
}
