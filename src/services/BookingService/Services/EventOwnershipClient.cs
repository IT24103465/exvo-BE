using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Exvo.BookingService.Services;

public interface IEventOwnershipClient
{
    Task<bool> IsOwnerAsync(int eventId, ClaimsPrincipal user, CancellationToken cancellationToken);
}

public sealed class EventOwnershipClient(IHttpClientFactory httpClientFactory, IHttpContextAccessor httpContextAccessor) : IEventOwnershipClient
{
    public async Task<bool> IsOwnerAsync(int eventId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var token = await httpContextAccessor.HttpContext!.GetTokenAsync("access_token");
        var client = httpClientFactory.CreateClient("CatalogService");
        if (string.IsNullOrWhiteSpace(token)) return false;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync($"/api/catalog/events/{eventId}/ownership", cancellationToken);
        return response.StatusCode == HttpStatusCode.OK;
    }
}
