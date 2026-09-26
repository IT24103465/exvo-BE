using Exvo.BookingService.Models;
using Microsoft.EntityFrameworkCore;

namespace Exvo.BookingService.Data;

public class BookingDbContext(DbContextOptions<BookingDbContext> options) : DbContext(options)
{
    public DbSet<SeatingPlan> SeatingPlans => Set<SeatingPlan>();
    public DbSet<SeatingSection> SeatingSections => Set<SeatingSection>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingItem> BookingItems => Set<BookingItem>();
    public DbSet<SeatHold> SeatHolds => Set<SeatHold>();
    public DbSet<SeatHoldItem> SeatHoldItems => Set<SeatHoldItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SeatingPlan>(entity =>
        {
            entity.HasKey(plan => plan.Id);
            entity.Property(plan => plan.Name).HasMaxLength(200).IsRequired();
            entity.Property(plan => plan.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.HasIndex(plan => plan.EventId).IsUnique();
            entity.HasMany(plan => plan.Sections).WithOne(section => section.SeatingPlan)
                .HasForeignKey(section => section.SeatingPlanId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(plan => plan.Seats).WithOne(seat => seat.SeatingPlan)
                .HasForeignKey(seat => seat.SeatingPlanId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SeatingSection>(entity =>
        {
            entity.HasKey(section => section.Id);
            entity.Property(section => section.Name).HasMaxLength(200).IsRequired();
            entity.Property(section => section.StartingRowLabel).HasMaxLength(20).IsRequired();
            entity.Property(section => section.Price).HasPrecision(18, 2);
            entity.HasIndex(section => new { section.SeatingPlanId, section.DisplayOrder });
            entity.HasMany(section => section.Seats).WithOne(seat => seat.SeatingSection)
                .HasForeignKey(seat => seat.SeatingSectionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Seat>(entity =>
        {
            entity.HasKey(seat => seat.Id);
            entity.Property(seat => seat.SeatCode).HasMaxLength(50).IsRequired();
            entity.Property(seat => seat.RowLabel).HasMaxLength(20).IsRequired();
            entity.Property(seat => seat.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(seat => seat.Price).HasPrecision(18, 2);
            entity.HasIndex(seat => new { seat.SeatingPlanId, seat.SeatCode }).IsUnique();
            entity.HasIndex(seat => seat.SeatingPlanId);
            entity.HasIndex(seat => seat.SeatingSectionId);
            entity.HasIndex(seat => seat.Status);
            entity.HasIndex(seat => seat.HoldExpiresAtUtc);
            entity.Property(seat => seat.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<Booking>(entity =>
        {
            entity.HasKey(booking => booking.Id);
            entity.Property(booking => booking.BookingReference).HasMaxLength(100).IsRequired();
            entity.Property(booking => booking.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(booking => booking.TotalAmount).HasPrecision(18, 2);
            entity.Property(booking => booking.Currency).HasMaxLength(3).IsRequired();
            entity.HasIndex(booking => booking.BookingReference).IsUnique();
            entity.HasMany(booking => booking.Items).WithOne(item => item.Booking)
                .HasForeignKey(item => item.BookingId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BookingItem>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.SeatCode).HasMaxLength(50).IsRequired();
            entity.Property(item => item.UnitPrice).HasPrecision(18, 2);
            entity.HasIndex(item => item.SeatId);
            entity.HasOne(item => item.Seat).WithMany().HasForeignKey(item => item.SeatId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        });

        modelBuilder.Entity<SeatHold>(entity =>
        {
            entity.HasKey(hold => hold.Id);
            entity.Property(hold => hold.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.HasIndex(hold => new { hold.EventId, hold.Status, hold.ExpiresAtUtc });
            entity.HasMany(hold => hold.Items).WithOne(item => item.SeatHold)
                .HasForeignKey(item => item.SeatHoldId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SeatHoldItem>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.SeatHoldId, item.SeatId }).IsUnique();
            entity.HasOne(item => item.Seat).WithMany().HasForeignKey(item => item.SeatId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
