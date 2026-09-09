using Microsoft.EntityFrameworkCore;
using Exvo.CatalogService.Models;

namespace Exvo.CatalogService.Data
{
    public class CatalogDbContext : DbContext
    {
        public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options)
        {
        }

        public DbSet<Event> Events { get; set; }
        public DbSet<Category> Categories { get; set; }
        public DbSet<Venue> Venues { get; set; }
        public DbSet<TicketTier> TicketTiers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ── Category ──
            modelBuilder.Entity<Category>(entity =>
            {
                entity.ToTable("categories");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Name).IsUnique();
            });

            // ── Venue ──
            modelBuilder.Entity<Venue>(entity =>
            {
                entity.ToTable("venues");
                entity.HasKey(e => e.Id);
            });

            // ── Event ──
            modelBuilder.Entity<Event>(entity =>
            {
                entity.ToTable("events");
                entity.HasKey(e => e.Id);

                entity.HasOne(e => e.Category)
                      .WithMany(c => c.Events)
                      .HasForeignKey(e => e.CategoryId)
                      .OnDelete(DeleteBehavior.SetNull);

                entity.HasOne(e => e.Venue)
                      .WithMany(v => v.Events)
                      .HasForeignKey(e => e.VenueId)
                      .OnDelete(DeleteBehavior.SetNull);

                entity.HasIndex(e => e.UserId);
                entity.HasIndex(e => e.Status);
                entity.HasIndex(e => e.EventDate);
            });

            // ── TicketTier ──
            modelBuilder.Entity<TicketTier>(entity =>
            {
                entity.ToTable("ticket_tiers");
                entity.HasKey(e => e.Id);

                entity.HasOne(e => e.Event)
                      .WithMany(ev => ev.TicketTiers)
                      .HasForeignKey(e => e.EventId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // ── Seed default categories ──
            modelBuilder.Entity<Category>().HasData(
                new Category { Id = 1, Name = "Concert", Description = "Live music performances" },
                new Category { Id = 2, Name = "Festival", Description = "Multi-day cultural or music events" },
                new Category { Id = 3, Name = "Conference", Description = "Professional and industry conferences" },
                new Category { Id = 4, Name = "Sports", Description = "Sporting events and competitions" },
                new Category { Id = 5, Name = "Theater", Description = "Theater and stage performances" },
                new Category { Id = 6, Name = "Comedy", Description = "Stand-up comedy and comedy shows" },
                new Category { Id = 7, Name = "Exhibition", Description = "Art and trade exhibitions" },
                new Category { Id = 8, Name = "Workshop", Description = "Hands-on learning workshops" }
            );
        }
    }
}
