using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebPos.Data;

namespace WebPos.Services;

/// <summary>
/// ID cards (barcode, magstripe or RFID badges read by a keyboard-wedge reader) identify a user by the code they type.
/// Only a hash of the code is stored.
/// </summary>
public static class IdCard
{
    public const int MinLength = 6;

    public static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim())));
}

/// <summary>A manager who approved a void or price override.</summary>
public record Approver(string UserId, string Name);

public record StaffMember(
    string Id, string UserName, string DisplayName, string? Role,
    bool IsActive, bool HasCard, bool IsLockedOut, bool MustChangePassword);

/// <summary>What the Users page edits. Blank <see cref="Password"/> and <see cref="CardCode"/> keep the current ones.</summary>
public class UserForm
{
    public string? Id { get; set; }
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = Roles.Cashier;
    public bool IsActive { get; set; } = true;
    public string Password { get; set; } = "";
    public bool MustChangePassword { get; set; } = true;
    public string CardCode { get; set; } = "";
    public bool RemoveCard { get; set; }
    public bool HasCard { get; set; }

    public bool IsNew => Id is null;
}

public static class StaffQueries
{
    /// <summary>Active users holding one of <paramref name="roles"/>, checked against the database rather than
    /// the sign-in cookie, which can be up to 12 hours old.</summary>
    public static IQueryable<PosUser> ActiveUsersInRoles(this PosDbContext db, string[] roles) =>
        db.Users.Where(u => u.IsActive && db.UserRoles.Any(ur => ur.UserId == u.Id
            && db.Roles.Any(r => r.Id == ur.RoleId && roles.Contains(r.Name!))));
}

/// <summary>
/// User accounts. Interactive pages live for a whole session, so each operation gets its own DI scope
/// (and so its own UserManager and DbContext) instead of one shared by every click.
/// </summary>
public class StaffService(IDbContextFactory<PosDbContext> dbFactory, IServiceScopeFactory scopes, TimeProvider time)
{
    /// <summary>Slows down guessing card codes or passwords in the approval dialog.</summary>
    public static TimeSpan FailureDelay { get; set; } = TimeSpan.FromSeconds(1);

    public async Task<List<StaffMember>> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.DisplayName)
            .Select(u => new
            {
                u.Id, u.UserName, u.DisplayName, u.IsActive, u.MustChangePassword, u.LockoutEnd,
                HasCard = u.CardHash != null,
                Role = db.UserRoles.Where(ur => ur.UserId == u.Id).Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name).FirstOrDefault(),
            })
            .ToListAsync();

        var now = time.GetUtcNow();
        return users.Select(u => new StaffMember(u.Id, u.UserName ?? "", u.DisplayName, u.Role,
            u.IsActive, u.HasCard, u.LockoutEnd > now, u.MustChangePassword)).ToList();
    }

    /// <summary>Creates or updates a user. Returns the problems that stopped the save; empty when it succeeded.</summary>
    public async Task<IReadOnlyList<string>> SaveAsync(UserForm form, string actingUserId)
    {
        var userName = form.UserName.Trim();
        var displayName = form.DisplayName.Trim();
        var card = form.CardCode.Trim();

        var errors = new List<string>();
        if (userName.Length == 0) errors.Add("Enter a user name.");
        if (displayName.Length == 0) errors.Add("Enter a display name.");
        if (!Roles.All.Contains(form.Role)) errors.Add("Choose a role.");
        if (form.IsNew && form.Password.Length == 0) errors.Add("Enter a password for the new user.");
        if (card.Length is > 0 and < IdCard.MinLength) errors.Add($"ID card codes must be at least {IdCard.MinLength} characters.");
        if (errors.Count > 0) return errors;

        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PosUser>>();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>(); // the same context the UserManager saves through
        await using var tx = await db.Database.BeginTransactionAsync();

        var user = form.IsNew ? new PosUser() : await users.FindByIdAsync(form.Id!);
        if (user is null) return ["This user no longer exists."];
        var currentRole = form.IsNew ? null : (await users.GetRolesAsync(user)).FirstOrDefault();

        var roleChanged = currentRole != form.Role;
        var deactivated = user.IsActive && !form.IsActive;
        if (!form.IsNew && user.Id == actingUserId && (roleChanged || deactivated))
            errors.Add("You can't change your own role or deactivate your own account.");
        else if (currentRole == Roles.SuperAdmin && user.IsActive && (roleChanged || deactivated)
                 && !await db.ActiveUsersInRoles([Roles.SuperAdmin]).AnyAsync(u => u.Id != user.Id))
            errors.Add("There must always be at least one active superadmin.");

        var cardHash = card.Length > 0 ? IdCard.Hash(card) : null;
        if (cardHash is not null
            && await db.Users.Where(u => u.CardHash == cardHash && u.Id != user.Id).Select(u => u.DisplayName).FirstOrDefaultAsync() is { } owner)
            errors.Add($"This ID card is already assigned to {owner}.");

        if (form.Password.Length > 0)
        {
            foreach (var validator in users.PasswordValidators)
                errors.AddRange((await validator.ValidateAsync(users, user, form.Password)).Errors.Select(e => e.Description));
        }
        if (errors.Count > 0) return errors;

        var cardChanged = cardHash is not null || (form.RemoveCard && user.CardHash is not null);
        user.UserName = userName;
        user.DisplayName = displayName;
        user.IsActive = form.IsActive;
        if (cardHash is not null) user.CardHash = cardHash;
        else if (form.RemoveCard) user.CardHash = null;
        if (form.Password.Length > 0) user.MustChangePassword = form.MustChangePassword;

        var result = form.IsNew ? await users.CreateAsync(user, form.Password) : await users.UpdateAsync(user);
        if (!result.Succeeded) return Describe(result);

        if (!form.IsNew && form.Password.Length > 0)
        {
            // AddIdentityCore registers no token providers, so there is no reset token; replace the password directly.
            if (!(result = await users.RemovePasswordAsync(user)).Succeeded) return Describe(result);
            if (!(result = await users.AddPasswordAsync(user, form.Password)).Succeeded) return Describe(result);
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
        }

        if (roleChanged)
        {
            if (currentRole is not null && !(result = await users.RemoveFromRoleAsync(user, currentRole)).Succeeded) return Describe(result);
            if (!(result = await users.AddToRoleAsync(user, form.Role)).Succeeded) return Describe(result);
        }

        // Ends the user's other sessions at their next check, so a lost card or an old role stops working.
        if (!form.IsNew && (roleChanged || deactivated || cardChanged)) await users.UpdateSecurityStampAsync(user);

        await tx.CommitAsync();
        return [];
    }

    public async Task<(Approver? Approver, string? Error)> VerifyManagerCardAsync(string code)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PosUser>>();
        var hash = IdCard.Hash(code);
        var user = code.Trim().Length == 0 ? null : await users.Users.FirstOrDefaultAsync(u => u.CardHash == hash);
        if (user is null) return await FailAsync("Card not recognized.");
        if (await users.IsLockedOutAsync(user)) return (null, "This account is locked. Try again in a few minutes.");
        return await CheckManagerAsync(users, user);
    }

    public async Task<(Approver? Approver, string? Error)> VerifyManagerPasswordAsync(string userName, string password)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PosUser>>();
        var user = await users.FindByNameAsync(userName.Trim());
        if (user is null) return await FailAsync("Invalid user name or password.");
        if (await users.IsLockedOutAsync(user)) return (null, "This account is locked. Try again in a few minutes.");
        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user); // counts towards the same lockout as the login page
            return await FailAsync("Invalid user name or password.");
        }
        await users.ResetAccessFailedCountAsync(user);
        return await CheckManagerAsync(users, user);
    }

    private static async Task<(Approver?, string?)> CheckManagerAsync(UserManager<PosUser> users, PosUser user)
    {
        if (!user.IsActive) return (null, $"{user.DisplayName}'s account is deactivated.");
        var roles = await users.GetRolesAsync(user);
        if (!roles.Any(Roles.Managers.Contains)) return (null, $"{user.DisplayName} is not a manager.");
        return (new Approver(user.Id, user.DisplayName), null);
    }

    private static async Task<(Approver?, string?)> FailAsync(string error)
    {
        await Task.Delay(FailureDelay);
        return (null, error);
    }

    private static List<string> Describe(IdentityResult result) => result.Errors.Select(e => e.Description).ToList();
}
