namespace WebPos.Data;

public class Item
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Barcode { get; set; }
    public string? Category { get; set; }
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
