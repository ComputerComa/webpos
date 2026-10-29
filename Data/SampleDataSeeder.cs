using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace WebPos.Data;

/// <summary>
/// Optional starter data, switched on with Database:SeedSampleItems and Database:SeedSampleUsers.
/// Items are only added to an empty table, and each sample user only if that user name is free.
/// </summary>
public static class SampleDataSeeder
{
    /// <summary>
    /// A small convenience-store catalog with typical US prices. Barcodes are UPC-A codes in number system 4
    /// (reserved for a retailer's own in-store labels, so they cannot clash with manufacturer barcodes);
    /// bananas use their standard produce PLU code.
    /// </summary>
    public static IReadOnlyList<Item> Items() =>
    [
        Item("Spring Water 16.9 oz", "400000001104", "Beverages", 1.29m),
        Item("Cola 20 oz Bottle", "400000001203", "Beverages", 2.49m),
        Item("Energy Drink 16 oz Can", "400000001302", "Beverages", 3.29m),
        Item("Whole Milk 1 gal", "400000001401", "Dairy", 4.29m),
        Item("Large Eggs 12 ct", "400000001500", "Dairy", 3.79m),
        Item("White Sandwich Bread 20 oz", "400000001609", "Bakery", 2.99m),
        Item("Spaghetti 16 oz", "400000001708", "Grocery", 1.89m),
        Item("Black Beans 15 oz Can", "400000001807", "Grocery", 1.29m),
        Item("Instant Ramen Noodles 3 oz", "400000001906", "Grocery", 0.89m),
        Item("Potato Chips 8 oz", "400000002002", "Snacks", 4.49m),
        Item("Milk Chocolate Bar 1.55 oz", "400000002101", "Snacks", 1.79m),
        Item("Bananas (each)", "4011", "Produce", 0.29m),
        Item("Paper Towels 2 Roll", "400000002200", "Household", 4.99m),
        Item("AA Alkaline Batteries 4 pk", "400000002309", "Household", 6.99m),
        Item("Toothpaste 4 oz", "400000002408", "Personal Care", 3.49m),
    ];

    public static async Task SeedItemsAsync(PosDbContext db, CancellationToken ct = default)
    {
        if (await db.Items.AnyAsync(ct)) return;
        db.Items.AddRange(Items());
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Demo staff with well-known passwords and ID card codes, for a local sandbox only.</summary>
    public static IReadOnlyList<(string UserName, string DisplayName, string Role, string Password, string CardCode)> Users() =>
    [
        ("alice", "Alice", Roles.Cashier, "cashier123", "100000001"),
        ("bob", "Bob", Roles.Cashier, "cashier123", "100000002"),
        ("maria", "Maria", Roles.Manager, "manager123", "200000001"),
    ];

    public static async Task SeedUsersAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<PosUser>>();
        foreach (var (userName, displayName, role, password, cardCode) in Users())
        {
            if (await userManager.FindByNameAsync(userName) is not null) continue;
            var user = new PosUser { UserName = userName, DisplayName = displayName, CardHash = Services.IdCard.Hash(cardCode) };
            IdentitySeeder.Check(await userManager.CreateAsync(user, password), $"create user '{userName}'");
            IdentitySeeder.Check(await userManager.AddToRoleAsync(user, role), "assign role");
        }
    }

    private static Item Item(string name, string barcode, string category, decimal price) =>
        new() { Name = name, Barcode = barcode, Category = category, Price = price };
}
