using System.Security.Claims;
using WebPos.Data;

namespace WebPos.Services;

/// <summary>Authorization policies; each one also admits the roles above it (see <see cref="Roles"/>).</summary>
public static class Policies
{
    /// <summary>Checkout and the transaction list: every role.</summary>
    public const string Sell = "Sell";

    /// <summary>Reports, items, and approving voids and price overrides: managers and superadmins.</summary>
    public const string Manage = "Manage";

    /// <summary>User accounts: superadmins only.</summary>
    public const string Admin = "Admin";
}

public static class PosClaims
{
    public const string DisplayNameType = "pos:display_name";

    public static string UserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Not signed in.");

    public static string DisplayName(this ClaimsPrincipal user) =>
        user.FindFirstValue(DisplayNameType) is { Length: > 0 } name ? name : user.Identity?.Name ?? "";

    public static string? Role(this ClaimsPrincipal user) => Roles.All.LastOrDefault(user.IsInRole);

    public static bool CanManage(this ClaimsPrincipal user) => Roles.Managers.Any(user.IsInRole);
}
