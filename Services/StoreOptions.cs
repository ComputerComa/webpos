namespace WebPos.Services;

public class StoreOptions
{
    public const string Section = "Store";

    public string Name { get; set; } = "WebPos";

    /// <summary>Sales tax as a fraction, e.g. 0.0825 for 8.25%. Applied to the whole subtotal.</summary>
    public decimal TaxRate { get; set; }

    /// <summary>Culture used for money and date formatting in the UI, e.g. "en-US".</summary>
    public string Culture { get; set; } = "en-US";

    /// <summary>IANA or Windows time zone id that defines the store's business day. Empty = server local time.</summary>
    public string? TimeZone { get; set; }
}
