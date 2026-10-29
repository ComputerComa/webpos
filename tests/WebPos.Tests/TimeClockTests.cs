using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using WebPos.Data;
using WebPos.Printing;
using WebPos.Reports;
using WebPos.Services;

namespace WebPos.Tests;

[Collection(PostgresCollection.Name)]
public class TimeClockTests(PostgresFixture db)
{
    private static readonly IOptions<StoreOptions> Store =
        Options.Create(new StoreOptions { Name = "Test Store", TimeZone = "America/New_York" });

    // 09:00 in New York (EDT).
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 29, 13, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Sales_need_an_open_shift_and_shifts_show_on_time_cards()
    {
        var user = await db.AddUserAsync(Roles.Cashier, clockedIn: false);
        var item = new Item { Name = "Clock item " + Guid.NewGuid().ToString("N")[..8], Price = 1m };
        await using (var ctx = db.CreateDbContext())
        {
            ctx.Items.Add(item);
            await ctx.SaveChangesAsync();
        }
        var checkout = new CheckoutService(db, Store, _time);
        Task Sell() => checkout.CompleteSaleAsync(new CheckoutRequest(user.Id, PaymentMethod.Card, 0m, [new(item.Id, 1)]));

        var ex = await Assert.ThrowsAsync<CheckoutException>(Sell);
        Assert.Equal("Clock in before making sales.", ex.Message);

        var clock = new TimeClock(db, _time);
        var staleTab = new TimeClock(db, _time);
        var changes = 0;
        clock.Changed += () => changes++;
        await clock.LoadAsync(user.Id);
        await staleTab.LoadAsync(user.Id);
        Assert.False(clock.IsClockedIn);

        await clock.ClockInAsync();
        Assert.True(clock.IsClockedIn);
        await staleTab.ClockInAsync();                          // still showed "clocked out": no second shift
        Assert.True(staleTab.IsClockedIn);
        await using (var ctx = db.CreateDbContext())
        {
            Assert.Equal(1, await ctx.Shifts.CountAsync(s => s.UserId == user.Id));
        }
        await Sell();

        _time.Advance(TimeSpan.FromHours(8.5));
        Assert.Equal(TimeSpan.FromHours(8.5), clock.Elapsed);
        await clock.ClockOutAsync();
        Assert.False(clock.IsClockedIn);
        Assert.Equal(3, changes);                               // load, clock in, clock out
        await Assert.ThrowsAsync<CheckoutException>(Sell);

        var stillIn = await db.AddUserAsync(Roles.Manager, clockedIn: false);
        var other = new TimeClock(db, _time);
        await other.LoadAsync(stillIn.Id);
        await other.ClockInAsync();                             // 17:30, still on the clock

        var reports = new ReportService(db, new StoreClock(Store, _time), Store,
            Options.Create(new PrinterOptions { Width = 80, LinesPerPage = 66 }));
        var day = new DateOnly(2026, 10, 29);
        var text = await reports.BuildAsync(new ReportRequest(ReportKind.TimeCards, day, day));
        var lines = text.Split("\r\n");
        Assert.Contains(lines, l => l.StartsWith(user.DisplayName) && l.Contains("2026-10-29 Thu") && l.Contains("09:00")
                                    && l.Contains("17:30") && l.TrimEnd().EndsWith("8:30"));
        Assert.Contains(lines, l => l.StartsWith(stillIn.DisplayName) && l.Contains("17:30") && l.Contains("on the clock"));
        Assert.DoesNotContain(user.DisplayName, await reports.BuildAsync(new ReportRequest(ReportKind.TimeCards, day.AddDays(1), day.AddDays(1))));
    }
}
