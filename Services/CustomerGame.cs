using WebPos.Data;

namespace WebPos.Services;

/// <param name="Smudged">The barcode won't scan; the cashier has to type the number.</param>
public record BasketLine(int ItemId, string Name, string Barcode, decimal Price, int Quantity, bool Smudged);

/// <summary>Money off one unit of an item, which the cashier applies as a price change.</summary>
public record Coupon(int ItemId, string ItemName, decimal Discount, decimal NewPrice);

/// <param name="ChangesMindAbout">An item the customer puts back once everything is scanned.</param>
/// <param name="CashHanded">For cash customers, what they hand over.</param>
public record Customer(
    int Number, string Name, string Greeting, IReadOnlyList<BasketLine> Basket,
    PaymentMethod PaysWith, decimal? CashHanded, Coupon? Coupon, BasketLine? ChangesMindAbout, decimal ExpectedTotal);

public record GameResult(int CustomerNumber, int SaleId, TimeSpan Time, int Points, IReadOnlyList<string> Mistakes)
{
    public bool Perfect => Mistakes.Count == 0;
}

/// <summary>Makes up customers from the catalog. Pass a seeded <see cref="Random"/> for the same customers every time.</summary>
public class CustomerGenerator(IReadOnlyList<Item> catalog, decimal taxRate, Random random)
{
    private static readonly string[] Names =
    [
        "Ava", "Ben", "Carlos", "Dana", "Eli", "Fatima", "Gus", "Hana", "Ivan", "Jada", "Kofi", "Lena", "Marco", "Nia",
        "Omar", "Priya", "Quinn", "Rosa", "Sam", "Tariq", "Uma", "Vic", "Wren", "Xavier", "Yuki", "Zoe",
    ];

    private static readonly string[] Greetings =
    [
        "Hi there!", "Morning!", "Just these, please.", "In a bit of a hurry...", "How's your day going?",
        "Found everything I needed!", "Can I get a bag?", "Busy today, huh?",
    ];

    /// <param name="twists">Smudged barcodes, coupons and customers who change their mind.</param>
    public virtual Customer Next(int number, bool twists)
    {
        var scannable = catalog.Where(i => i.IsActive && !string.IsNullOrWhiteSpace(i.Barcode)).ToList();
        if (scannable.Count == 0) throw new InvalidOperationException("Add some items with barcodes first.");

        var basket = scannable.OrderBy(_ => random.Next()).Take(Pick(1, 1, 2, 2, 3, 3, 3, 4, 4, 5, 6)).Select(item =>
        {
            var plu = Barcodes.IsPlu(item.Barcode!);
            var quantity = plu ? random.Next(2, 7) : Pick(1, 1, 1, 1, 1, 1, 2, 2, 3);
            var smudged = twists && !plu && random.NextDouble() < 0.12;
            return new BasketLine(item.Id, item.Name, item.Barcode!.Trim(), item.Price, quantity, smudged);
        }).ToList();

        Coupon? coupon = null;
        if (twists && random.NextDouble() < 0.2 && basket.Where(l => l.Price >= 1.25m).ToList() is { Count: > 0 } couponable)
        {
            var line = couponable[random.Next(couponable.Count)];
            var discount = line.Price >= 2.5m ? Pick(0.50m, 1.00m) : 0.25m;
            coupon = new Coupon(line.ItemId, line.Name, discount, line.Price - discount);
            basket[basket.IndexOf(line)] = line with { Quantity = 1 }; // one coupon, one item
        }

        BasketLine? putBack = null;
        if (twists && basket.Count >= 2 && random.NextDouble() < 0.15)
        {
            var candidates = basket.Where(l => l.ItemId != coupon?.ItemId).ToList();
            putBack = candidates[random.Next(candidates.Count)];
        }

        var expected = Expected(basket, coupon, putBack);
        var total = SaleCalculator.Totals(expected.Values.Select(e => (e.UnitPrice, e.Quantity)), taxRate).Total;
        var paysCash = random.NextDouble() < 0.45;

        return new Customer(number, Names[random.Next(Names.Length)], Greetings[random.Next(Greetings.Length)], basket,
            paysCash ? PaymentMethod.Cash : PaymentMethod.Card, paysCash ? CashFor(total) : null, coupon, putBack, total);
    }

    /// <summary>What should be rung up: item, quantity and price, after any coupon and put-back.</summary>
    public static Dictionary<int, (int Quantity, decimal UnitPrice)> Expected(IEnumerable<BasketLine> basket, Coupon? coupon, BasketLine? putBack)
    {
        var expected = new Dictionary<int, (int Quantity, decimal UnitPrice)>();
        foreach (var line in basket)
        {
            var quantity = line.Quantity - (line.ItemId == putBack?.ItemId ? 1 : 0);
            if (quantity > 0) expected[line.ItemId] = (quantity, line.ItemId == coupon?.ItemId ? coupon.NewPrice : line.Price);
        }
        return expected;
    }

    /// <summary>Exact change, or the next round amount or banknote above the total, like real customers.</summary>
    private decimal CashFor(decimal total)
    {
        var options = new[] { total, Math.Ceiling(total), Math.Ceiling(total / 5) * 5, Math.Ceiling(total / 10) * 10, Math.Ceiling(total / 20) * 20 }
            .Where(amount => amount >= total).Distinct().ToList();
        return options[random.Next(options.Count)];
    }

    private T Pick<T>(params T[] choices) => choices[random.Next(choices.Length)];
}

/// <summary>Customer mode on the checkout page: one customer at a time, scored when their sale is completed.
/// Lives in <see cref="PosSession"/>, so it survives moving between pages.</summary>
public class CustomerGame
{
    public bool IsOn { get; set; }

    /// <summary>Smudged barcodes, coupons and customers who change their mind.</summary>
    public bool Twists { get; set; } = true;

    public Customer? Current { get; private set; }
    public DateTime ArrivedAt { get; private set; }

    /// <summary>The customer has asked to put an item back (shown once everything in the basket is scanned).</summary>
    public bool MindChanged { get; private set; }

    /// <summary>Score for the customer just served; null while serving one.</summary>
    public GameResult? LastResult { get; private set; }

    public int Served { get; private set; }
    public int Perfect { get; private set; }
    public int Points { get; private set; }
    public int Streak { get; private set; }
    public int BestStreak { get; private set; }
    public TimeSpan TotalTime { get; private set; }

    public void NextCustomer(CustomerGenerator generator, DateTime now)
    {
        Current = generator.Next(Served + 1, Twists);
        ArrivedAt = now;
        MindChanged = false;
        LastResult = null;
    }

    /// <summary>Call after each change to the cart: once everything is scanned, the customer may put something back.</summary>
    public void Observe(Cart cart)
    {
        if (Current?.ChangesMindAbout is null || MindChanged) return;
        if (Current.Basket.All(line => Scanned(cart, line.ItemId) >= line.Quantity)) MindChanged = true;
    }

    public static int Scanned(Cart cart, int itemId) => cart.Lines.Where(l => l.ItemId == itemId).Sum(l => l.Quantity);

    /// <summary>Marks the current customer's sale and returns the score.</summary>
    public GameResult Finish(Transaction sale, DateTime now)
    {
        var customer = Current ?? throw new InvalidOperationException("No customer is waiting.");
        var mistakes = Mistakes(customer, sale);
        var time = now - ArrivedAt;

        // 100 for a perfect sale, plus 2 points per second under par (5 s per item + 15 s to pay), up to 50.
        // Each mistake costs 25.
        var units = customer.Basket.Sum(l => l.Quantity);
        var par = TimeSpan.FromSeconds(units * 5 + 15);
        var points = mistakes.Count == 0
            ? 100 + Math.Clamp((int)((par - time).TotalSeconds * 2), 0, 50)
            : Math.Max(0, 100 - 25 * mistakes.Count);

        Served++;
        Points += points;
        TotalTime += time;
        if (mistakes.Count == 0)
        {
            Perfect++;
            BestStreak = Math.Max(BestStreak, ++Streak);
        }
        else
        {
            Streak = 0;
        }

        Current = null;
        return LastResult = new GameResult(customer.Number, sale.Id, time, points, mistakes);
    }

    public static List<string> Mistakes(Customer customer, Transaction sale)
    {
        var mistakes = new List<string>();
        var expected = CustomerGenerator.Expected(customer.Basket, customer.Coupon, customer.ChangesMindAbout);
        var rung = sale.Items.GroupBy(i => i.ItemId ?? 0).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

        foreach (var line in customer.Basket)
        {
            var want = expected.TryGetValue(line.ItemId, out var e) ? e.Quantity : 0;
            var got = rung.GetValueOrDefault(line.ItemId);
            if (got < want) mistakes.Add($"Missed {want - got} x {line.Name}");
            else if (got > want && line.ItemId == customer.ChangesMindAbout?.ItemId && got == line.Quantity)
                mistakes.Add($"Didn't take the {line.Name} off");
            else if (got > want) mistakes.Add($"Rang up {got - want} x {line.Name} too many");
        }
        foreach (var item in sale.Items.Where(i => customer.Basket.All(l => l.ItemId != i.ItemId)))
            mistakes.Add($"{item.Name} wasn't in the basket");

        if (customer.Coupon is { } coupon && expected.ContainsKey(coupon.ItemId)
            && !sale.Items.Any(i => i.ItemId == coupon.ItemId && i.UnitPrice == coupon.NewPrice))
            mistakes.Add($"Coupon not applied: {coupon.ItemName} should be {coupon.NewPrice:C}");
        if (sale.Items.FirstOrDefault(i => i.CatalogPrice is not null && i.ItemId != customer.Coupon?.ItemId) is { } changed)
            mistakes.Add($"Changed the price of {changed.Name} without a coupon");

        if (sale.PaymentMethod != customer.PaysWith)
            mistakes.Add($"Customer paid by {customer.PaysWith.ToString().ToLowerInvariant()}, rang up as {sale.PaymentMethod.ToString().ToLowerInvariant()}");
        else if (customer.CashHanded is { } handed && sale.AmountTendered != handed)
            mistakes.Add($"Customer handed you {handed:C}, you entered {sale.AmountTendered:C}");

        return mistakes;
    }
}
