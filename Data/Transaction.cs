namespace WebPos.Data;

public enum PaymentMethod
{
    Cash,
    Card,
    Other
}

public enum TransactionStatus
{
    Completed,
    Voided
}

public class Transaction
{
    /// <summary>Also used as the receipt number.</summary>
    public int Id { get; set; }

    /// <summary>Always UTC.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The user who rang up the sale. Null only for sales recorded before user accounts were linked.</summary>
    public string? CashierId { get; set; }
    public PosUser? Cashier { get; set; }

    /// <summary>The cashier's display name at the time of sale, used by the transaction list and reports.</summary>
    public string CashierName { get; set; } = "";

    public PaymentMethod PaymentMethod { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public decimal AmountTendered { get; set; }
    public decimal ChangeDue { get; set; }

    public TransactionStatus Status { get; set; } = TransactionStatus.Completed;
    public DateTime? VoidedAt { get; set; }

    /// <summary>Display name of the manager who voided or approved the void.</summary>
    public string? VoidedBy { get; set; }

    public List<TransactionItem> Items { get; set; } = [];
}
