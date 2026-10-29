using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using WebPos.Data;

namespace WebPos.Services;

public static class IdentitySetup
{
    public static IServiceCollection AddPosIdentity(this IServiceCollection services)
    {
        services.AddIdentityCore<PosUser>(options =>
            {
                // Relaxed from Identity's defaults (which also require an uppercase letter and a symbol)
                // so the seeded default password "superadmin12345" is accepted.
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<PosDbContext>()
            .AddClaimsPrincipalFactory<PosClaimsPrincipalFactory>()
            .AddSignInManager<PosSignInManager>();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Sell, p => p.RequireRole(Roles.All))
            .AddPolicy(Policies.Manage, p => p.RequireRole(Roles.Managers))
            .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.SuperAdmin));
        return services;
    }
}

/// <summary>Blocks deactivated users from signing in, by password or by ID card.</summary>
public class PosSignInManager(
        UserManager<PosUser> userManager,
        IHttpContextAccessor contextAccessor,
        IUserClaimsPrincipalFactory<PosUser> claimsFactory,
        IOptions<IdentityOptions> optionsAccessor,
        ILogger<SignInManager<PosUser>> logger,
        IAuthenticationSchemeProvider schemes,
        IUserConfirmation<PosUser> confirmation)
    : SignInManager<PosUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(PosUser user) => user.IsActive && await base.CanSignInAsync(user);
}

/// <summary>Adds the display name to the sign-in cookie, so pages can show it without a database lookup.</summary>
public class PosClaimsPrincipalFactory(
        UserManager<PosUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<PosUser, IdentityRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(PosUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(PosClaims.DisplayNameType, user.DisplayName));
        return identity;
    }
}
