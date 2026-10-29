namespace WebPos.Data;

/// <summary>
/// A line on a sale. Name, barcode and price are snapshots taken at the time of sale,
/// so later edits to the <see cref="Item"/> never change historic transactions.
/// </summary>
public class TransactionItem
{
    public int Id { get; set; }

    public int TransactionId { get; set; }
    public Transaction Transaction { get; set; } = null!;

    /// <summary>Link back to the catalog item; null if the item was later deleted.</summary>
    public int? ItemId { get; set; }
    public Item? Item { get; set; }

    public string Name { get; set; } = "";
    public string? Barcode { get; set; }

    /// <summary>The price charged per unit.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>The catalog price when a manager overrode it; null if the catalog price was charged.</summary>
    public decimal? CatalogPrice { get; set; }

    /// <summary>Display name of the manager who approved the price override.</summary>
    public string? PriceOverriddenBy { get; set; }

    public int Quantity { get; set; }
    public decimal LineTotal { get; set; }
}
