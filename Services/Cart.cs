using WebPos.Data;

namespace WebPos.Services;

/// <summary>The in-progress sale on the checkout screen. Prices here are for display only;
/// <see cref="CheckoutService"/> re-reads them from the database when the sale is completed.</summary>
public class Cart
{
    private readonly List<CartLine> _lines = [];

    public IReadOnlyList<CartLine> Lines => _lines;

    public bool IsEmpty => _lines.Count == 0;

    public int ItemCount => _lines.Sum(l => l.Quantity);

    /// <summary>Adds to the item's line, or starts a new one. A line with an approved price override is never
    /// added to, so the approval only covers the quantity the manager saw.</summary>
    public CartLine Add(Item item, int quantity = 1)
    {
        var line = _lines.FirstOrDefault(l => l.ItemId == item.Id && l.PriceOverride is null);
        if (line is null)
        {
            line = new CartLine { ItemId = item.Id, Name = item.Name, CatalogPrice = item.Price };
            _lines.Add(line);
        }
        line.Quantity += quantity;
        return line;
    }

    public void OverridePrice(CartLine line, decimal price, Approver approver)
    {
        line.PriceOverride = SaleCalculator.RoundMoney(price);
        line.OverrideApprovedBy = approver;
    }

    public void ClearOverride(CartLine line)
    {
        line.PriceOverride = null;
        line.OverrideApprovedBy = null;
    }

    public void SetQuantity(CartLine line, int quantity)
    {
        if (quantity <= 0) _lines.Remove(line);
        else line.Quantity = quantity;
    }

    public void Remove(CartLine line) => _lines.Remove(line);

    public void Clear() => _lines.Clear();

    public SaleTotals Totals(decimal taxRate) =>
        SaleCalculator.Totals(_lines.Select(l => (l.UnitPrice, l.Quantity)), taxRate);
}

public class CartLine
{
    public int ItemId { get; init; }
    public string Name { get; set; } = "";
    public decimal CatalogPrice { get; set; }
    public decimal? PriceOverride { get; set; }
    public Approver? OverrideApprovedBy { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice => PriceOverride ?? CatalogPrice;
    public decimal LineTotal => SaleCalculator.LineTotal(UnitPrice, Quantity);
}
