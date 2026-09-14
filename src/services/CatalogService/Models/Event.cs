using System.Text.Json.Serialization;

namespace Exvo.CatalogService.Models
{
    public class Event
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string? Venue { get; set; }
        public decimal Price { get; set; }
        public DateTime EventDate { get; set; }
        public int CategoryId { get; set; }

        [JsonIgnore]
        public Category? Category { get; set; }

        public int OrganizerId { get; set; }
        public string? OrganizerName { get; set; }
        public string? ImageUrl { get; set; }
        public int AvailableTickets { get; set; }
        public string? TicketTiersJson { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}