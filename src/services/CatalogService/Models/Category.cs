using System.Text.Json.Serialization;

namespace Exvo.CatalogService.Models
{
    public class Category
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonIgnore]
        public ICollection<Event> Events { get; set; } = new List<Event>();
    }
}
