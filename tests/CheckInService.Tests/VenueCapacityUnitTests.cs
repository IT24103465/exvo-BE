using Exvo.CheckInService.Models.DTOs;
using Exvo.CheckInService.Services;
using Xunit;

namespace Exvo.CheckInService.Tests;

public class VenueCapacityUnitTests
{
    [Theory]
    [InlineData(
        "[{\"quantity\":20},{\"quantity\":80}]",
        50, 30, 100)]
    [InlineData(null, 100, 30, 100)]
    [InlineData("[]", 100, 150, 150)]
    [InlineData("[]", 0, 0, 0)]
    [InlineData("invalid-json", 80, 20, 80)]
    public void ResolveVenueCapacity_ReturnsExpectedCapacity(
        string? tiersJson,
        int availableTickets,
        int soldTickets,
        int expectedCapacity)
    {
        // Arrange: Create event data with controlled inputs.
        var eventData = new CatalogEventResponse(
            Id: 10,
            Title: "Test Event",
            Description: null,
            Location: null,
            Venue: "Test Venue",
            Price: 2500m,
            EventDate: new DateTime(2026, 12, 1),
            UtcOffsetMinutes: 330,
            CategoryId: 1,
            OrganizerId: 100,
            OrganizerName: null,
            ImageUrl: null,
            AvailableTickets: availableTickets,
            TicketTiersJson: tiersJson,
            CreatedAt: new DateTime(2026, 10, 1),
            IsHidder: false
        );

        // Act: Call the actual production calculation.
        var result = EventAnalyticsService.ResolveVenueCapacity(
            eventData, soldTickets);

        // Assert: Verify the calculated capacity.
        Assert.Equal(expectedCapacity, result);
    }
}