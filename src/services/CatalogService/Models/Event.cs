using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Exvo.CatalogService.Models
{
    [Table("events")]
    public class Event
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>Organizer's user ID from Auth service.</summary>
        public int UserId { get; set; }

        /// <summary>Denormalized organizer display name (avoids cross-service call).</summary>
        [MaxLength(255)]
        public string OrganizerName { get; set; } = string.Empty;

        [Required]
        [MaxLength(255)]
        public string Title { get; set; } = string.Empty;

        [MaxLength(255)]
        public string? ArtistOrOrganizer { get; set; }

        // ── Category relationship ──
        public int? CategoryId { get; set; }

        [ForeignKey("CategoryId")]
        public virtual Category? Category { get; set; }

        /// <summary>Kept for backward compatibility with frontend.</summary>
        [MaxLength(100)]
        public string CategoryName { get; set; } = "Concert";

        // ── Venue relationship ──
        public int? VenueId { get; set; }

        [ForeignKey("VenueId")]
        public virtual Venue? Venue { get; set; }

        /// <summary>Kept for backward compatibility with frontend.</summary>
        [MaxLength(255)]
        public string VenueName { get; set; } = string.Empty;

        [Required]
        [MaxLength(50)]
        public string EventDate { get; set; } = string.Empty;

        [MaxLength(50)]
        public string EventTime { get; set; } = "19:00";

        [Column(TypeName = "decimal(18,2)")]
        public decimal MinPrice { get; set; } = 0;

        public int TotalCapacity { get; set; } = 0;

        [Column(TypeName = "longtext")]
        public string? CoverImage { get; set; }

        [Column(TypeName = "text")]
        public string? Description { get; set; }

        [MaxLength(50)]
        public string Status { get; set; } = "Published";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // ── Ticket tiers relationship ──
        public virtual ICollection<TicketTier> TicketTiers { get; set; } = new List<TicketTier>();
    }
}
