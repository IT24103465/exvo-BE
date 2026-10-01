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
    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://catalog")
        };
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
