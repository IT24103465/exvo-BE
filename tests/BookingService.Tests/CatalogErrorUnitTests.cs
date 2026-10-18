using System.Net;
using Exvo.BookingService.Services;
using Xunit;

public class CatalogErrorUnitTests
{
    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public async Task GetTicketCatalog_ServerError_ThrowsException(
        int statusCode)
    {
        // Arrange: Fake a CatalogService error response.
        var factory = new ErrorHttpClientFactory(
            (HttpStatusCode)statusCode);

        var service = new EventInventoryCatalogClient(factory);

        // Act and Assert: The method must throw an HTTP exception.
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.GetTicketCatalogAsync(
                5, CancellationToken.None));

        Assert.Equal(
            (HttpStatusCode)statusCode,
            exception.StatusCode);
    }
    [Fact]
    public async Task GetTicketCatalog_EventNotFound_ReturnsNull()
    {
        // Arrange: CatalogService says the event does not exist.
        var factory = new ErrorHttpClientFactory(
            HttpStatusCode.NotFound);

        var service = new EventInventoryCatalogClient(factory);

        // Act: Request the missing event.
        var result = await service.GetTicketCatalogAsync(
            999, CancellationToken.None);

        // Assert: No catalog should be returned.
        Assert.Null(result);
    }
    private sealed class ErrorHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpStatusCode _statusCode;

        public ErrorHttpClientFactory(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new ErrorHttpHandler(_statusCode))
            {
                BaseAddress = new Uri("http://catalog")
            };
        }
    }

    private sealed class ErrorHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public ErrorHttpHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new HttpResponseMessage(_statusCode));
        }
    }
}