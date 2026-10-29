using WebPos.Data;

namespace WebPos.Services;

public readonly record struct SaleTotals(decimal Subtotal, decimal Tax, decimal Total);

public static class SaleCalculator
{
    public static decimal RoundMoney(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static decimal LineTotal(decimal unitPrice, int quantity) => RoundMoney(unitPrice * quantity);

    public static SaleTotals Totals(IEnumerable<(decimal UnitPrice, int Quantity)> lines, decimal taxRate)
    {
        var subtotal = lines.Sum(l => LineTotal(l.UnitPrice, l.Quantity));
        var tax = RoundMoney(subtotal * taxRate);
        return new SaleTotals(subtotal, tax, subtotal + tax);
    }

    /// <summary>
    /// Works out what is recorded as tendered and the change due. Cash must cover the total;
    /// for card and other methods the exact total is charged and no change is given.
    /// </summary>
    public static (decimal Tendered, decimal Change) Settle(PaymentMethod method, decimal total, decimal tendered)
    {
        if (method != PaymentMethod.Cash) return (total, 0m);

        tendered = RoundMoney(tendered);
        if (tendered < total)
            throw new CheckoutException($"Amount tendered ({tendered:0.00}) is less than the total ({total:0.00}).");
        return (tendered, tendered - total);
    }
}

public class CheckoutException(string message) : Exception(message);
