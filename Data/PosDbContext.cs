using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace WebPos.Data;

public class PosDbContext(DbContextOptions<PosDbContext> options) : IdentityDbContext<PosUser>(options)
{
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<TransactionItem> TransactionItems => Set<TransactionItem>();
    public DbSet<Shift> Shifts => Set<Shift>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(12, 2);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // Identity tables

        modelBuilder.Entity<PosUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
            e.Property(u => u.CardHash).HasMaxLength(64);
            e.HasIndex(u => u.CardHash).IsUnique();
        });

        modelBuilder.Entity<Item>(e =>
        {
            e.Property(i => i.Name).HasMaxLength(200).IsRequired();
            e.Property(i => i.Barcode).HasMaxLength(64);
            e.Property(i => i.Category).HasMaxLength(100);
            e.HasIndex(i => i.Barcode).IsUnique();
            e.HasIndex(i => i.Name);
        });

        modelBuilder.Entity<Transaction>(e =>
        {
            e.Property(t => t.PaymentMethod).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.CashierName).HasMaxLength(100).IsRequired();
            e.Property(t => t.VoidedBy).HasMaxLength(100);
            e.HasIndex(t => t.CreatedAt);
            e.HasOne(t => t.Cashier).WithMany().HasForeignKey(t => t.CashierId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(t => t.Items).WithOne(i => i.Transaction).HasForeignKey(i => i.TransactionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Shift>(e =>
        {
            e.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(s => s.UserId).IsUnique().HasFilter("\"ClockOut\" IS NULL"); // one open shift per user
            e.HasIndex(s => s.ClockIn);
        });

        modelBuilder.Entity<TransactionItem>(e =>
        {
            e.Property(i => i.Name).HasMaxLength(200).IsRequired();
            e.Property(i => i.Barcode).HasMaxLength(64);
            e.Property(i => i.PriceOverriddenBy).HasMaxLength(100);
            e.HasOne(i => i.Item).WithMany().HasForeignKey(i => i.ItemId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
