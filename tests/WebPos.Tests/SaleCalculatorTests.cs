using WebPos.Data;
using WebPos.Services;

namespace WebPos.Tests;

public class SaleCalculatorTests
{
    [Fact]
    public void Totals_sum_lines_and_round_tax_half_away_from_zero()
    {
        // 2 x 2.50 + 1 x 1.25 = 6.25; 6.25 * 0.10 = 0.625 -> 0.63
        var totals = SaleCalculator.Totals([(2.50m, 2), (1.25m, 1)], 0.10m);

        Assert.Equal(6.25m, totals.Subtotal);
        Assert.Equal(0.63m, totals.Tax);
        Assert.Equal(6.88m, totals.Total);
    }

    [Fact]
    public void Totals_without_tax_equal_subtotal()
    {
        var totals = SaleCalculator.Totals([(1.95m, 3)], 0m);

        Assert.Equal(new SaleTotals(5.85m, 0m, 5.85m), totals);
    }

    [Fact]
    public void Cash_gives_change()
    {
        var (tendered, change) = SaleCalculator.Settle(PaymentMethod.Cash, 7.35m, 20m);

        Assert.Equal(20m, tendered);
        Assert.Equal(12.65m, change);
    }

    [Fact]
    public void Cash_must_cover_the_total()
    {
        var ex = Assert.Throws<CheckoutException>(() => SaleCalculator.Settle(PaymentMethod.Cash, 7.35m, 7.34m));
        Assert.Contains("less than the total", ex.Message);
    }

    [Theory]
    [InlineData(PaymentMethod.Card)]
    [InlineData(PaymentMethod.Other)]
    public void Non_cash_charges_exact_total_with_no_change(PaymentMethod method)
    {
        var (tendered, change) = SaleCalculator.Settle(method, 7.35m, 0m);

        Assert.Equal(7.35m, tendered);
        Assert.Equal(0m, change);
    }

    [Fact]
    public void Cart_merges_repeat_scans_and_removes_lines_at_zero()
    {
        var cart = new Cart();
        var coffee = new Item { Id = 1, Name = "Coffee", Price = 2.50m };

        cart.Add(coffee);
        var line = cart.Add(coffee, 2);
        Assert.Single(cart.Lines);
        Assert.Equal(3, line.Quantity);
        Assert.Equal(7.50m, cart.Totals(0m).Total);

        cart.SetQuantity(line, 0);
        Assert.True(cart.IsEmpty);
    }
}

public class SampleDataTests
{
    [Fact]
    public void Sample_items_have_unique_valid_barcodes_and_prices()
    {
        var items = WebPos.Data.SampleDataSeeder.Items();

        Assert.InRange(items.Count, 10, 15);
        Assert.Equal(items.Count, items.Select(i => i.Barcode).Distinct().Count());
        Assert.All(items, i => Assert.True(i.Price > 0, i.Name));
        foreach (var code in items.Select(i => i.Barcode!).Where(b => b.Length == 12))
        {
            var digits = code.Select(c => c - '0').ToArray();
            var sum = digits[..11].Select((d, i) => i % 2 == 0 ? d * 3 : d).Sum();
            Assert.True((10 - sum % 10) % 10 == digits[11], $"bad UPC check digit: {code}");
        }
    }
}
