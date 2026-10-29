using Microsoft.AspNetCore.Identity;

namespace WebPos.Data;

public class PosUser : IdentityUser
{
    /// <summary>Shown in the header, on sales and on reports; the user name is only for logging in.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Deactivated users can't log in or make sales. Users are never deleted, so their sales keep a link to them.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Set for accounts created with a well-known password, such as the seeded superadmin.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>SHA-256 of the user's ID card code (see <see cref="Services.IdCard"/>), or null if no card is assigned.
    /// Only the hash is stored, so the code can't be read back from the database.</summary>
    public string? CardHash { get; set; }
}

/// <summary>Every user has exactly one of these roles. Each role can do everything the one before it can.</summary>
public static class Roles
{
    public const string Cashier = "Cashier";
    public const string Manager = "Manager";
    public const string SuperAdmin = "SuperAdmin";

    public static readonly string[] All = [Cashier, Manager, SuperAdmin];

    /// <summary>Roles that may approve voids and price overrides, see reports and edit items.</summary>
    public static readonly string[] Managers = [Manager, SuperAdmin];
}
