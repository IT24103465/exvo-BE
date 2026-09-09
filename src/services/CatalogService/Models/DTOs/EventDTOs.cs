namespace Exvo.CatalogService.Models.DTOs
{
    // ── Ticket Tier DTO (matches frontend shape) ──
    public record TicketTierDto(
        string Id,
        string Name,
        decimal Price,
        int Quantity
    );

    // ── Event Requests ──
    public record CreateEventRequest(
        string Title,
        string? ArtistOrOrganizer,
        string Category,
        string Date,
        string? Time,
        string Venue,
        List<TicketTierDto>? TicketTiers,
        string? CoverImage,
        string? Description
    );

    public record UpdateEventRequest(
        string Title,
        string? ArtistOrOrganizer,
        string Category,
        string Date,
        string? Time,
        string Venue,
        List<TicketTierDto>? TicketTiers,
        string? CoverImage,
        string? Description,
        string? Status
    );

    // ── Event Response (same shape frontend expects) ──
    public record EventResponse(
        int Id,
        int UserId,
        string OrganizerName,
        string Title,
        string? ArtistOrOrganizer,
        string Category,
        string Date,
        string Time,
        string Venue,
        List<TicketTierDto> TicketTiers,
        decimal MinPrice,
        int TotalCapacity,
        string? CoverImage,
        string? Description,
        string Status,
        DateTime CreatedAt,
        DateTime UpdatedAt
    );

    // ── Category ──
    public record CategoryResponse(
        int Id,
        string Name,
        string? Description,
        string? IconUrl
    );

    // ── Venue ──
    public record VenueResponse(
        int Id,
        string Name,
        string? Address,
        string? City,
        int TotalCapacity,
        string? Description
    );
}
