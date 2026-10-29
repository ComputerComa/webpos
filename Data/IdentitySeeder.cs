using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace WebPos.Data;

public class DefaultAdminOptions
{
    public const string Section = "DefaultAdmin";

    public string UserName { get; set; } = "superadmin";
    public string Password { get; set; } = "superadmin12345";
}

/// <summary>Creates the roles and, on a database with no users yet, the default superadmin account.</summary>
public static class IdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
                Check(await roleManager.CreateAsync(new IdentityRole(role)), $"create role '{role}'");
        }

        // Only seed into an empty user table, so a removed or renamed admin never comes back with the default password.
        var userManager = services.GetRequiredService<UserManager<PosUser>>();
        if (await userManager.Users.AnyAsync()) return;

        var admin = services.GetRequiredService<IOptions<DefaultAdminOptions>>().Value;
        var user = new PosUser { UserName = admin.UserName, DisplayName = "Superadmin", MustChangePassword = true };
        Check(await userManager.CreateAsync(user, admin.Password), $"create user '{admin.UserName}'");
        Check(await userManager.AddToRoleAsync(user, Roles.SuperAdmin), "assign role");
    }

    internal static void Check(IdentityResult result, string action)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Identity seeding failed to {action}: {string.Join(" ", result.Errors.Select(e => e.Description))}");
    }
}
