using Microsoft.Extensions.Options;

namespace WebPos.Services;

/// <summary>
/// Converts between UTC (what the database stores) and the store's local time,
/// so that "today" and date-range reports follow the store's business day.
/// </summary>
public class StoreClock(IOptions<StoreOptions> options, TimeProvider time)
{
    public TimeZoneInfo TimeZone { get; } = string.IsNullOrWhiteSpace(options.Value.TimeZone)
        ? TimeZoneInfo.Local
        : TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public DateTime Now => ToLocal(UtcNow);

    public DateOnly Today => DateOnly.FromDateTime(Now);

    public DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone);

    /// <summary>UTC start (inclusive) and end (exclusive) of the local days <paramref name="from"/>..<paramref name="to"/>.</summary>
    public (DateTime StartUtc, DateTime EndUtc) ToUtcRange(DateOnly from, DateOnly to)
    {
        if (to < from) (from, to) = (to, from);
        return (StartOfDayUtc(from), StartOfDayUtc(to.AddDays(1)));
    }

    private DateTime StartOfDayUtc(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Midnight can be skipped by a DST jump in a few zones; step forward until it is valid.
        while (TimeZone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(local, TimeZone);
    }
}
