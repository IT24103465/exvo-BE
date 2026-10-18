using System.Net;
using System.Text;
using Exvo.BookingService.Services;

public class EventInventoryCatalogClientTests
{
    [Fact]
    public async Task ANullTicketQuantityIsReadAsUnlimited()
    {
        var handler = new StaticJsonHandler("""
            {"availableTickets":0,"ticketTiersJson":"[{\"id\":\"tier-temp\",\"name\":\"General\",\"price\":2500,\"quantity\":null}]"}
            """);
        var client = new EventInventoryCatalogClient(new StaticHttpClientFactory(handler));

        var catalog = await client.GetTicketCatalogAsync(5, CancellationToken.None);

        Assert.NotNull(catalog);
        var tier = Assert.Single(catalog.Tiers);
        Assert.True(tier.IsUnlimited);
        Assert.Equal(0, tier.Quantity);
        Assert.Null(tier.TicketTierId);
        Assert.Equal("General", tier.Name);
        Assert.Equal(2500m, tier.Price);
    }

    [Fact]
    public async Task TicketCatalogMapsFiniteTierDetailsFromJsonArray()
    {
        var handler = new StaticJsonHandler("""
            {"availableTickets":12,"ticketTiersJson":[{"id":7,"name":"VIP","price":2500,"quantity":4}]}
            """);
        var factory = new StaticHttpClientFactory(handler);
        var client = new EventInventoryCatalogClient(factory);

        var catalog = await client.GetTicketCatalogAsync(23, CancellationToken.None);

        Assert.NotNull(catalog);
        Assert.Equal(23, catalog.EventId);
        Assert.Equal(12, catalog.AvailableTickets);
        var tier = Assert.Single(catalog.Tiers);
        Assert.Equal(7, tier.TicketTierId);
        Assert.Equal("VIP", tier.Name);
        Assert.Equal(2500m, tier.Price);
        Assert.Equal(4, tier.Quantity);
        Assert.False(tier.IsUnlimited);
        Assert.Equal("CatalogService", factory.CreatedClientName);
        Assert.Equal(HttpMethod.Get, handler.RequestMethod);
        Assert.Equal("/api/catalog/events/23", handler.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task TicketCatalogCreatesGeneralAdmissionTierWhenTierListIsEmpty()
    {
        var handler = new StaticJsonHandler("""
            {"AvailableTickets":"8","Price":"1750","TicketTiersJson":"[]"}
            """);
        var client = new EventInventoryCatalogClient(new StaticHttpClientFactory(handler));

        var catalog = await client.GetTicketCatalogAsync(5, CancellationToken.None);

        Assert.NotNull(catalog);
        Assert.Equal(8, catalog.AvailableTickets);
        var tier = Assert.Single(catalog.Tiers);
        Assert.Null(tier.TicketTierId);
        Assert.Equal("General Admission", tier.Name);
        Assert.Equal(1750m, tier.Price);
        Assert.Equal(8, tier.Quantity);
        Assert.False(tier.IsUnlimited);
    }

    [Fact]
    public async Task TicketCatalogDoesNotCreateGeneralAdmissionTierWithoutInventory()
    {
        var handler = new StaticJsonHandler("""
            {"availableTickets":0,"ticketTiersJson":[]}
            """);
        var client = new EventInventoryCatalogClient(new StaticHttpClientFactory(handler));

        var catalog = await client.GetTicketCatalogAsync(5, CancellationToken.None);

        Assert.NotNull(catalog);
        Assert.Equal(0, catalog.AvailableTickets);
        Assert.Empty(catalog.Tiers);
    }

    [Fact]
    public async Task TicketCatalogFallsBackWhenTierListIsMissing()
    {
        var handler = new StaticJsonHandler("""
            {"availableTickets":3,"price":2000}
            """);
        var client = new EventInventoryCatalogClient(new StaticHttpClientFactory(handler));

        var catalog = await client.GetTicketCatalogAsync(5, CancellationToken.None);

        Assert.NotNull(catalog);
        var tier = Assert.Single(catalog.Tiers);
        Assert.Equal("General Admission", tier.Name);
        Assert.Equal(2000m, tier.Price);
        Assert.Equal(3, tier.Quantity);
    }

    [Fact]
    public async Task TicketCatalogSupportsTicketTierIdAliasAndTierDefaults()
    {
        var handler = new StaticJsonHandler("""
            {"ticketTiersJson":[{"ticketTierId":"9"}]}
            """);
        var client = new EventInventoryCatalogClient(new StaticHttpClientFactory(handler));

        var catalog = await client.GetTicketCatalogAsync(5, CancellationToken.None);

        Assert.NotNull(catalog);
        var tier = Assert.Single(catalog.Tiers);
        Assert.Equal(9, tier.TicketTierId);
        Assert.Equal("General Admission", tier.Name);
        Assert.Equal(0m, tier.Price);
        Assert.Equal(0, tier.Quantity);
        Assert.True(tier.IsUnlimited);
    }

    [Fact]
    public async Task TicketCatalogPrefersIdOverTicketTierIdAlias()
    {
        var handler = new StaticJsonHandler("""
            {"ticketTiersJson":[{"id":7,"ticketTierId":9}]}
            """);
        var client = new EventInventoryCatalogClient(new StaticHttpClientFactory(handler));

        var catalog = await client.GetTicketCatalogAsync(5, CancellationToken.None);

        Assert.NotNull(catalog);
        Assert.Equal(7, Assert.Single(catalog.Tiers).TicketTierId);
    }

    [Fact]
    public async Task TicketCatalogMapsNegativeTierQuantityAsUnlimited()
    {
        var handler = new StaticJsonHandler("""
            {"ticketTiersJson":[{"name":"Unlimited","quantity":-1}]}
            """);
        var client = new EventInventoryCatalogClient(new StaticHttpClientFactory(handler));

        var catalog = await client.GetTicketCatalogAsync(5, CancellationToken.None);

        Assert.NotNull(catalog);
        var tier = Assert.Single(catalog.Tiers);
        Assert.True(tier.IsUnlimited);
        Assert.Equal(0, tier.Quantity);
    }

    [Fact]
    public async Task EventSnapshotMapsAllCatalogFieldsAndUsesSnapshotEndpoint()
    {
        var handler = new StaticJsonHandler("""
            {"title":"Live Show","eventDate":"2026-11-05T20:00:00Z","utcOffsetMinutes":330,"venue":"Colombo Hall","artistOrOrganizer":"Band","category":"Music","coverImage":"cover.jpg"}
            """);
        var factory = new StaticHttpClientFactory(handler);
        var client = new EventInventoryCatalogClient(factory);

        var snapshot = await client.GetEventSnapshotAsync(31, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(31, snapshot.EventId);
        Assert.Equal("Live Show", snapshot.Title);
        Assert.Equal(new DateTime(2026, 11, 5, 20, 0, 0, DateTimeKind.Utc), snapshot.EventDate);
        Assert.Equal(330, snapshot.UtcOffsetMinutes);
        Assert.Equal("Colombo Hall", snapshot.Venue);
        Assert.Equal("Band", snapshot.ArtistOrOrganizer);
        Assert.Equal("Music", snapshot.Category);
        Assert.Equal("cover.jpg", snapshot.CoverImage);
        Assert.Equal("CatalogService", factory.CreatedClientName);
        Assert.Equal(HttpMethod.Get, handler.RequestMethod);
        Assert.Equal("/api/catalog/events/31/ticket-snapshot", handler.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task EventSnapshotUsesFallbacksAndCatalogFieldAliases()
    {
        var handler = new StaticJsonHandler("""
            {"location":"Main Hall","organizerName":"Organizer","categoryName":"Theatre","imageUrl":"poster.jpg"}
            """);
        var client = new EventInventoryCatalogClient(new StaticHttpClientFactory(handler));

        var snapshot = await client.GetEventSnapshotAsync(42, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(42, snapshot.EventId);
        Assert.Equal("Event #42", snapshot.Title);
        Assert.Equal(DateTime.MinValue, snapshot.EventDate);
        Assert.Null(snapshot.UtcOffsetMinutes);
        Assert.Equal("Main Hall", snapshot.Venue);
        Assert.Equal("Organizer", snapshot.ArtistOrOrganizer);
        Assert.Equal("Theatre", snapshot.Category);
        Assert.Equal("poster.jpg", snapshot.CoverImage);
    }

    [Fact]
    public async Task EventSnapshotPrefersCanonicalFieldsOverLegacyAliases()
    {
        var handler = new StaticJsonHandler("""
            {"venue":"Primary Hall","location":"Fallback Hall","artistOrOrganizer":"Primary Artist","organizerName":"Fallback Organizer","category":"Primary Category","categoryName":"Fallback Category","coverImage":"primary.jpg","imageUrl":"fallback.jpg"}
            """);
        var client = new EventInventoryCatalogClient(new StaticHttpClientFactory(handler));

        var snapshot = await client.GetEventSnapshotAsync(42, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("Primary Hall", snapshot.Venue);
        Assert.Equal("Primary Artist", snapshot.ArtistOrOrganizer);
        Assert.Equal("Primary Category", snapshot.Category);
        Assert.Equal("primary.jpg", snapshot.CoverImage);
    }

    [Fact]
    public async Task EventSnapshotUsesFallbacksForValuesWithUnexpectedJsonTypes()
    {
        var handler = new StaticJsonHandler("""
            {"title":42,"eventDate":42,"utcOffsetMinutes":{},"venue":[],"location":"Fallback Hall","artistOrOrganizer":42,"organizerName":"Fallback Organizer","category":false,"categoryName":"Fallback Category","coverImage":{},"imageUrl":"fallback.jpg"}
            """);
        var client = new EventInventoryCatalogClient(new StaticHttpClientFactory(handler));

        var snapshot = await client.GetEventSnapshotAsync(17, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("Event #17", snapshot.Title);
        Assert.Equal(DateTime.MinValue, snapshot.EventDate);
        Assert.Null(snapshot.UtcOffsetMinutes);
        Assert.Equal("Fallback Hall", snapshot.Venue);
        Assert.Equal("Fallback Organizer", snapshot.ArtistOrOrganizer);
        Assert.Equal("Fallback Category", snapshot.Category);
        Assert.Equal("fallback.jpg", snapshot.CoverImage);
    }

    [Fact]
    public async Task EventSnapshotReturnsNullWhenCatalogReportsNotFound()
    {
        var handler = new StaticJsonHandler("{}", HttpStatusCode.NotFound);
        var client = new EventInventoryCatalogClient(new StaticHttpClientFactory(handler));

        var snapshot = await client.GetEventSnapshotAsync(404, CancellationToken.None);

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task EventSnapshotThrowsWhenCatalogReturnsServerError()
    {
        var handler = new StaticJsonHandler("{}", HttpStatusCode.ServiceUnavailable);
        var client = new EventInventoryCatalogClient(new StaticHttpClientFactory(handler));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetEventSnapshotAsync(5, CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public string? CreatedClientName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreatedClientName = name;
            return new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://catalog")
            };
        }
    }

    private sealed class StaticJsonHandler(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpMethod? RequestMethod { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestMethod = request.Method;
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
