using Exvo.CatalogService.Data;
using Exvo.CatalogService.Models;
using Microsoft.EntityFrameworkCore;

namespace Exvo.CatalogService;

public static class EventSchedule
{
    // Older events were entered in Sri Lanka local time without a stored offset.
    public const int LegacyUtcOffsetMinutes = 330;

    public static IQueryable<Event> Upcoming(IQueryable<Event> events, DateTime utcNow) =>
        events.Where(e => e.EventDate.AddMinutes(-(e.UtcOffsetMinutes ?? LegacyUtcOffsetMinutes)) >= utcNow);

    public static IQueryable<Event> Expired(IQueryable<Event> events, DateTime utcNow) =>
        events.Where(e => e.EventDate.AddMinutes(-(e.UtcOffsetMinutes ?? LegacyUtcOffsetMinutes)) < utcNow);

    public static async Task HideExpiredAsync(CatalogDbContext db, DateTime utcNow)
    {
        var expiredEvents = await Expired(db.Events, utcNow)
            .Where(e => !e.IsHidder)
            .ToListAsync();

        if (expiredEvents.Count == 0) return;

        foreach (var evt in expiredEvents)
            evt.IsHidder = true;

        await db.SaveChangesAsync();
    }

    public static DateTime StartsAtUtc(Event evt) => DateTime.SpecifyKind(
        evt.EventDate.AddMinutes(-(evt.UtcOffsetMinutes ?? LegacyUtcOffsetMinutes)), DateTimeKind.Utc);
}
