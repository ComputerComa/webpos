namespace WebPos.Data;

/// <summary>One stretch on the clock. A user has at most one open shift (no <see cref="ClockOut"/>) at a time.</summary>
public class Shift
{
    public int Id { get; set; }

    public string UserId { get; set; } = "";
    public PosUser User { get; set; } = null!;

    /// <summary>Always UTC.</summary>
    public DateTime ClockIn { get; set; }

    /// <summary>Always UTC; null while the user is on the clock.</summary>
    public DateTime? ClockOut { get; set; }
}
