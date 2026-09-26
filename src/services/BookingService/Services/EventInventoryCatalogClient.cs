using System.Net;
using System.Text.Json;

namespace Exvo.BookingService.Services;

public interface IEventInventoryCatalogClient
{
    Task<EventTicketCatalog?> GetTicketCatalogAsync(int eventId, CancellationToken cancellationToken);
    Task<EventTicketSnapshot?> GetEventSnapshotAsync(int eventId, CancellationToken cancellationToken);
}

public sealed record EventTicketCatalog(int EventId, int AvailableTickets, IReadOnlyList<CatalogTicketTier> Tiers);
public sealed record CatalogTicketTier(int? TicketTierId, string Name, decimal Price, int Quantity);
public sealed record EventTicketSnapshot(
    int EventId,
    string Title,
    DateTime EventDate,
    int? UtcOffsetMinutes,
    string? Venue,
    string? ArtistOrOrganizer,
    string? Category,
    string? CoverImage);

public sealed class EventInventoryCatalogClient(IHttpClientFactory httpClientFactory) : IEventInventoryCatalogClient
{
    public async Task<EventTicketCatalog?> GetTicketCatalogAsync(int eventId, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("CatalogService");
        using var response = await client.GetAsync($"/api/catalog/events/{eventId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var availableTickets = GetInt(root, "availableTickets") ?? GetInt(root, "AvailableTickets") ?? 0;
        var price = GetDecimal(root, "price") ?? GetDecimal(root, "Price") ?? 0m;
        var tiers = ParseTiers(root);
        if (tiers.Count == 0 && availableTickets > 0)
        {
            tiers.Add(new CatalogTicketTier(null, "General Admission", price, availableTickets));
        }

        return new EventTicketCatalog(eventId, availableTickets, tiers);
    }

    public async Task<EventTicketSnapshot?> GetEventSnapshotAsync(int eventId, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("CatalogService");
        using var response = await client.GetAsync($"/api/catalog/events/{eventId}/ticket-snapshot", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        return new EventTicketSnapshot(
            eventId,
            GetString(root, "title") ?? $"Event #{eventId}",
            GetDateTime(root, "eventDate") ?? DateTime.MinValue,
            GetInt(root, "utcOffsetMinutes"),
            GetString(root, "venue") ?? GetString(root, "location"),
            GetString(root, "artistOrOrganizer") ?? GetString(root, "organizerName"),
            GetString(root, "category") ?? GetString(root, "categoryName"),
            GetString(root, "coverImage") ?? GetString(root, "imageUrl"));
    }

    private static List<CatalogTicketTier> ParseTiers(JsonElement root)
    {
        if (!TryGetProperty(root, "ticketTiersJson", out var tiersJson) && !TryGetProperty(root, "TicketTiersJson", out tiersJson))
            return [];

        JsonElement tiersElement;
        JsonDocument? tiersDocument = null;
        if (tiersJson.ValueKind == JsonValueKind.String)
        {
            var raw = tiersJson.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return [];
            tiersDocument = JsonDocument.Parse(raw);
            tiersElement = tiersDocument.RootElement;
        }
        else
        {
            tiersElement = tiersJson;
        }

        using (tiersDocument)
        {
            if (tiersElement.ValueKind != JsonValueKind.Array) return [];
            return tiersElement.EnumerateArray()
                .Select(tier => new CatalogTicketTier(
                    GetInt(tier, "id") ?? GetInt(tier, "ticketTierId"),
                    GetString(tier, "name") ?? "General Admission",
                    GetDecimal(tier, "price") ?? 0m,
                    Math.Max(0, GetInt(tier, "quantity") ?? 0)))
                .Where(tier => tier.Quantity > 0)
                .ToList();
        }
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static decimal? GetDecimal(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDecimal(out var value) => value,
            JsonValueKind.String when decimal.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static DateTime? GetDateTime(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.String when property.TryGetDateTime(out var value) => value,
            _ => null
        };
    }
}
