using WebPos.Data;
using WebPos.Services;

namespace WebPos.Tests;

public class CustomerGameTests
{
    private static readonly List<Item> Catalog =
    [
        new() { Id = 1, Name = "Cola", Barcode = "400000001203", Price = 2.49m },
        new() { Id = 2, Name = "Milk", Barcode = "400000001401", Price = 4.29m },
        new() { Id = 3, Name = "Bananas", Barcode = "4011", Price = 0.29m },
        new() { Id = 4, Name = "Gum", Barcode = "GUM-1", Price = 0.99m },
        new() { Id = 5, Name = "Batteries", Barcode = "400000002309", Price = 6.99m },
        new() { Id = 6, Name = "No barcode", Barcode = null, Price = 1m },
        new() { Id = 7, Name = "Retired", Barcode = "OLD-1", Price = 1m, IsActive = false },
    ];

    private static List<Customer> Customers(int seed, bool twists, int count = 300)
    {
        var generator = new CustomerGenerator(Catalog, 0.08m, new Random(seed));
        return Enumerable.Range(1, count).Select(n => generator.Next(n, twists)).ToList();
    }

    [Fact]
    public void Customers_are_believable_and_repeatable()
    {
        var customers = Customers(seed: 7, twists: true);

        foreach (var c in customers)
        {
            Assert.InRange(c.Basket.Count, 1, 5);
            Assert.Equal(c.Basket.Count, c.Basket.Select(l => l.ItemId).Distinct().Count());
            Assert.All(c.Basket, l => Assert.Contains(l.ItemId, new[] { 1, 2, 3, 4, 5 }));   // active, with a barcode
            Assert.All(c.Basket, l => Assert.True(l.Quantity >= 1));
            Assert.DoesNotContain(c.Basket, l => l.Smudged && Barcodes.IsPlu(l.Barcode)); // produce is typed anyway

            if (c.Coupon is { } coupon)
            {
                var line = Assert.Single(c.Basket, l => l.ItemId == coupon.ItemId);
                Assert.Equal(1, line.Quantity);
                Assert.Equal(line.Price - coupon.Discount, coupon.NewPrice);
                Assert.True(coupon.NewPrice > 0);
            }
            if (c.ChangesMindAbout is { } putBack)
            {
                Assert.Contains(putBack, c.Basket);
                Assert.NotEqual(c.Coupon?.ItemId, putBack.ItemId);
            }

            var expected = CustomerGenerator.Expected(c.Basket, c.Coupon, c.ChangesMindAbout);
            Assert.Equal(SaleCalculator.Totals(expected.Values.Select(e => (e.UnitPrice, e.Quantity)), 0.08m).Total, c.ExpectedTotal);
            if (c.PaysWith == PaymentMethod.Cash) Assert.True(c.CashHanded >= c.ExpectedTotal);
            else Assert.Null(c.CashHanded);
        }

        // Every twist and both payment methods turn up, and the same seed gives the same customers.
        Assert.Contains(customers, c => c.Coupon is not null);
        Assert.Contains(customers, c => c.ChangesMindAbout is not null);
        Assert.Contains(customers, c => c.Basket.Any(l => l.Smudged));
        Assert.Contains(customers, c => c.PaysWith == PaymentMethod.Cash && c.CashHanded > c.ExpectedTotal);
        Assert.Contains(customers, c => c.PaysWith == PaymentMethod.Card);
        Assert.Equal(
            customers.Select(c => (c.Name, c.ExpectedTotal, c.Basket.Count)),
            Customers(seed: 7, twists: true).Select(c => (c.Name, c.ExpectedTotal, c.Basket.Count)));

        Assert.All(Customers(seed: 7, twists: false), c =>
        {
            Assert.Null(c.Coupon);
            Assert.Null(c.ChangesMindAbout);
            Assert.DoesNotContain(c.Basket, l => l.Smudged);
        });
    }

    private static readonly BasketLine Cola = new(1, "Cola", "400000001203", 2.49m, 2, false);
    private static readonly BasketLine Milk = new(2, "Milk", "400000001401", 4.29m, 1, false);

    private static Transaction Sale(PaymentMethod method, decimal tendered, params TransactionItem[] items) =>
        new() { Id = 99, PaymentMethod = method, AmountTendered = tendered, Items = [.. items] };

    private static TransactionItem Line(BasketLine b, int quantity, decimal? price = null) => new()
    {
        ItemId = b.ItemId, Name = b.Name, Quantity = quantity, UnitPrice = price ?? b.Price, CatalogPrice = price is null ? null : b.Price,
    };

    [Fact]
    public void A_correct_quick_sale_scores_a_perfect_with_a_speed_bonus()
    {
        var game = new CustomerGame();
        var start = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var customer = new Customer(1, "Ava", "Hi!", [Cola, Milk], PaymentMethod.Cash, 20m, null, null, 9.27m);
        game.NextCustomer(new FixedGenerator(customer), start);

        // Par is 5 s per item (3) + 15 s = 30 s; done in 20 s, so 10 s under par = 20 bonus points.
        var result = game.Finish(Sale(PaymentMethod.Cash, 20m, Line(Cola, 2), Line(Milk, 1)), start.AddSeconds(20));

        Assert.True(result.Perfect);
        Assert.Equal(120, result.Points);
        Assert.Equal((1, 1, 120, 1), (game.Served, game.Perfect, game.Points, game.Streak));
        Assert.Null(game.Current);
    }

    [Fact]
    public void Mistakes_are_spelled_out_and_cost_points()
    {
        var putBackCola = new Customer(2, "Ben", "Hi!", [Cola, Milk], PaymentMethod.Cash, 10m,
            new Coupon(2, "Milk", 1.00m, 3.29m), Cola, 0m);

        var mistakes = CustomerGame.Mistakes(putBackCola, Sale(PaymentMethod.Card, 0m,
            Line(Cola, 2), Line(Milk, 1), new TransactionItem { ItemId = 4, Name = "Gum", Quantity = 1, UnitPrice = 0.99m }));
        Assert.Equal(
        [
            "Didn't take the Cola off",
            "Gum wasn't in the basket",
            "Coupon not applied: Milk should be $3.29",
            "Customer paid by cash, rang up as card",
        ], mistakes);

        // A single put-back item still rung up.
        var notRemoved = putBackCola with { Basket = [Cola with { Quantity = 1 }, Milk], ChangesMindAbout = Cola with { Quantity = 1 } };
        Assert.Contains("Didn't take the Cola off", CustomerGame.Mistakes(notRemoved, Sale(PaymentMethod.Cash, 10m, Line(Cola, 1), Line(Milk, 1, 3.29m))));

        // Everything right except the cash entered.
        Assert.Equal(["Customer handed you $10.00, you entered $5.78"],
            CustomerGame.Mistakes(putBackCola, Sale(PaymentMethod.Cash, 5.78m, Line(Cola, 1), Line(Milk, 1, 3.29m))));

        // One too many, a missed item, and a price change nobody asked for.
        var plain = new Customer(3, "Cy", "Hi!", [Cola, Milk], PaymentMethod.Card, null, null, null, 0m);
        var sloppy = Sale(PaymentMethod.Card, 0m, Line(Cola, 3, 1.00m));
        Assert.Equal(["Rang up 1 x Cola too many", "Missed 1 x Milk", "Changed the price of Cola without a coupon"],
            CustomerGame.Mistakes(plain, sloppy));

        var game = new CustomerGame();
        game.NextCustomer(new FixedGenerator(plain), DateTime.UtcNow);
        var result = game.Finish(sloppy, DateTime.UtcNow);
        Assert.Equal(25, result.Points);                      // 100 - 3 x 25, no speed bonus
        Assert.Equal(0, game.Streak);
    }

    [Fact]
    public void The_customer_changes_their_mind_once_everything_is_scanned()
    {
        var game = new CustomerGame();
        game.NextCustomer(new FixedGenerator(new Customer(1, "Dee", "Hi!", [Cola, Milk], PaymentMethod.Card, null, null, Milk, 0m)), DateTime.UtcNow);
        var cart = new Cart();

        cart.Add(new Item { Id = 1, Name = "Cola", Price = 2.49m }, 2);
        game.Observe(cart);
        Assert.False(game.MindChanged);

        cart.Add(new Item { Id = 2, Name = "Milk", Price = 4.29m });
        game.Observe(cart);
        Assert.True(game.MindChanged);

        cart.SetQuantity(cart.Lines[1], 0);
        game.Observe(cart);
        Assert.True(game.MindChanged);                       // stays changed
    }

    /// <summary>Always produces the given customer.</summary>
    private sealed class FixedGenerator(Customer customer) : CustomerGenerator([], 0m, new Random(0))
    {
        public override Customer Next(int number, bool twists) => customer;
    }
}
