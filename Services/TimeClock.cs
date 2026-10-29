using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebPos.Data;

namespace WebPos.Services;

/// <summary>
/// The signed-in user's clock-in state for one browser tab (scoped to the Blazor circuit). The header and the
/// checkout page both read it, and <see cref="Changed"/> keeps them in step.
/// </summary>
public class TimeClock(IDbContextFactory<PosDbContext> dbFactory, TimeProvider time)
{
    private string? userId;

    /// <summary>The open shift, or null when clocked out.</summary>
    public Shift? OpenShift { get; private set; }

    public bool IsClockedIn => OpenShift is not null;

    public event Action? Changed;

    /// <summary>Reads the user's state once per tab; later calls for the same user do nothing.</summary>
    public async Task LoadAsync(string forUserId)
    {
        if (userId == forUserId) return;
        userId = forUserId;
        await RefreshAsync();
    }

    public async Task ClockInAsync()
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Shifts.Add(new Shift { UserId = UserId, ClockIn = time.GetUtcNow().UtcDateTime });
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Already clocked in from another tab or till.
            }
        }
        await RefreshAsync();
    }

    public async Task ClockOutAsync()
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var now = time.GetUtcNow().UtcDateTime;
            await db.Shifts.Where(s => s.UserId == UserId && s.ClockOut == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ClockOut, now));
        }
        await RefreshAsync();
    }

    /// <summary>Time on the clock so far in the open shift.</summary>
    public TimeSpan Elapsed => OpenShift is null ? TimeSpan.Zero : time.GetUtcNow().UtcDateTime - OpenShift.ClockIn;

    private string UserId => userId ?? throw new InvalidOperationException("Call LoadAsync first.");

    private async Task RefreshAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        OpenShift = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == UserId && s.ClockOut == null);
        Changed?.Invoke();
    }
}
