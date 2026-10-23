using System.Security.Claims;
using Exvo.CatalogService;
using Exvo.CatalogService.Data;
using Exvo.CatalogService.Models;
using Microsoft.EntityFrameworkCore;

namespace Exvo.CatalogService.Tests;

public class CatalogRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Organizer", "1", true, 1)]
    [InlineData("Company", "25", true, 25)]
    [InlineData("Attendee", "1", true, null)]
    [InlineData("Organizer", "0", true, null)]
    [InlineData("Organizer", "-1", true, null)]
    [InlineData("Organizer", "bad", true, null)]
    [InlineData("Organizer", "1", false, null)]
    public void Organizer_id_requires_authenticated_organizer_and_positive_id(string role, string id, bool authenticated, int? expected)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.NameIdentifier, id)], authenticated ? "test" : null));
        Assert.Equal(role is "Organizer" or "Company", EventAccess.IsOrganizer(user));
        Assert.Equal(expected, EventAccess.OrganizerId(user));
    }

    [Fact]
    public void Subject_fallback_and_identifier_precedence_are_preserved()
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, "Organizer"), new("sub", "9") };
        Assert.Equal(9, EventAccess.OrganizerId(new(new ClaimsIdentity(claims, "test"))));
        claims.Add(new(ClaimTypes.NameIdentifier, "4"));
        Assert.Equal(4, EventAccess.OrganizerId(new(new ClaimsIdentity(claims, "test"))));
        Assert.Null(EventAccess.OrganizerId(new ClaimsPrincipal()));
        Assert.Null(EventAccess.OrganizerId(new(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Organizer")], "test"))));
    }

    [Theory]
    [InlineData(-840)]
    [InlineData(0)]
    [InlineData(330)]
    [InlineData(840)]
    public void Schedule_accepts_exact_boundary_and_future_but_rejects_past(int offset)
    {
        var local = DateTime.SpecifyKind(Now.AddMinutes(offset), DateTimeKind.Unspecified);
        Assert.Null(EventAccess.ValidateSchedule(local, offset, Now));
        Assert.Null(EventAccess.ValidateSchedule(local.AddTicks(1), offset, Now));
        Assert.Equal("Event date and time must be in the future (your local time).",
            EventAccess.ValidateSchedule(local.AddTicks(-1), offset, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-841)]
    [InlineData(841)]
    public void Schedule_rejects_missing_or_invalid_offsets(int? offset) =>
        Assert.Equal("Please provide a valid local event date, time, and UTC offset.",
            EventAccess.ValidateSchedule(DateTime.SpecifyKind(Now, DateTimeKind.Unspecified), offset, Now));

    [Fact]
    public void Schedule_rejects_default_nonlocal_and_overflowing_dates()
    {
        const string invalid = "Please provide a valid local event date, time, and UTC offset.";
        Assert.Equal(invalid, EventAccess.ValidateSchedule(default, 0, Now));
        Assert.Equal(invalid, EventAccess.ValidateSchedule(Now, 0, Now));
        Assert.Equal(invalid, EventAccess.ValidateSchedule(DateTime.SpecifyKind(Now, DateTimeKind.Local), 0, Now));
        Assert.Equal("Please provide a valid local event date and time.",
            EventAccess.ValidateSchedule(DateTime.MaxValue, -840, Now));
    }

    [Theory]
    [InlineData(null, 330)]
    [InlineData(0, 0)]
    [InlineData(-300, -300)]
    [InlineData(330, 330)]
    public void Event_queries_and_start_time_use_event_offset_or_legacy_offset(int? offset, int effectiveOffset)
    {
        var local = DateTime.SpecifyKind(Now.AddMinutes(effectiveOffset), DateTimeKind.Unspecified);
        var events = new[]
        {
            new Event { Id = 1, EventDate = local.AddTicks(-1), UtcOffsetMinutes = offset },
            new Event { Id = 2, EventDate = local, UtcOffsetMinutes = offset },
            new Event { Id = 3, EventDate = local.AddTicks(1), UtcOffsetMinutes = offset }
        };
        Assert.Equal(new[] { 2, 3 }, EventSchedule.Upcoming(events.AsQueryable(), Now).Select(e => e.Id));
        Assert.Equal(new[] { 1 }, EventSchedule.Expired(events.AsQueryable(), Now).Select(e => e.Id));
        var starts = EventSchedule.StartsAtUtc(events[1]);
        Assert.Equal(Now, starts);
        Assert.Equal(DateTimeKind.Utc, starts.Kind);
    }

    [Fact]
    public async Task Hiding_expired_events_persists_only_expired_visible_events_and_is_idempotent()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new CatalogDbContext(options);
        db.Events.AddRange(
            new Event { Id = 1, EventDate = Now.AddSeconds(-1), UtcOffsetMinutes = 0 },
            new Event { Id = 2, EventDate = Now, UtcOffsetMinutes = 0 },
            new Event { Id = 3, EventDate = Now.AddDays(1), UtcOffsetMinutes = 0 },
            new Event { Id = 4, EventDate = Now.AddDays(-1), UtcOffsetMinutes = 0, IsHidder = true });
        await db.SaveChangesAsync();
        await EventSchedule.HideExpiredAsync(db, Now);
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { 1, 4 }, await db.Events.Where(e => e.IsHidder).OrderBy(e => e.Id).Select(e => e.Id).ToArrayAsync());
        await EventSchedule.HideExpiredAsync(db, Now);
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
