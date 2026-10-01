using System.Security.Claims;

namespace Exvo.CatalogService;

public static class EventAccess
{
    public static bool IsOrganizer(ClaimsPrincipal user) => user.IsInRole("Organizer") || user.IsInRole("Company");

    public static int? OrganizerId(ClaimsPrincipal user)
    {
        var value = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value;
        return user.Identity?.IsAuthenticated == true && IsOrganizer(user) &&
            int.TryParse(value, out var id) && id > 0 ? id : null;
    }

    // EventDate is stored as local wall time. The request supplies the offset at that date,
    // rather than relying on the API server's timezone or changing existing stored dates.
    public static string? ValidateSchedule(DateTime date, int? utcOffsetMinutes, DateTimeOffset now)
    {
        if (date == default || date.Kind != DateTimeKind.Unspecified ||
            utcOffsetMinutes is null or < -840 or > 840)
            return "Please provide a valid local event date, time, and UTC offset.";

        try
        {
            var scheduled = new DateTimeOffset(date, TimeSpan.FromMinutes(utcOffsetMinutes.Value));
            return scheduled < now ? "Event date and time must be in the future (your local time)." : null;
        }
        catch (ArgumentException)
        {
            return "Please provide a valid local event date and time.";
        }
    }
}
