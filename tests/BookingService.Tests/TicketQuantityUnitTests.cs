using System.Net;
using System.Text;
using System.Text.Json;
using Exvo.BookingService.Services;
using Xunit;

public class TicketQuantityUnitTests
{
    [Theory]
    [InlineData(null, true, 0)]
    [InlineData(0, true, 0)]
    [InlineData(1, false, 1)]
    [InlineData(10, false, 10)]
    public async Task GetTicketCatalog_MapsQuantityCorrectly(
        int? inputQuantity,
        bool expectedUnlimited,
        int expectedQuantity)
    {
        // Arrange: Prepare a fake CatalogService response.
        var json = JsonSerializer.Serialize(new
        {
            availableTickets = 10,
            ticketTiersJson = new[]
            {
                new
                {
                    id = 1,
                    name = "VIP",
                    price = 2500,
                    quantity = inputQuantity
                }
            }
        });

        var factory = new StubHttpClientFactory(json);
        var service = new EventInventoryCatalogClient(factory);

        // Act: Call the actual production method.
        var result = await service.GetTicketCatalogAsync(
            5, CancellationToken.None);

        // Assert: Check the returned ticket details.
        Assert.NotNull(result);
        var tier = Assert.Single(result!.Tiers);

        Assert.Equal(expectedUnlimited, tier.IsUnlimited);
        Assert.Equal(expectedQuantity, tier.Quantity);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly string _json;

        public StubHttpClientFactory(string json)
        {
            _json = json;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new StubHttpHandler(_json))
            {
                BaseAddress = new Uri("http://catalog")
            };
        }
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubHttpHandler(string json)
        {
            _json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _json, Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }
}