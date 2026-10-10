using Microsoft.EntityFrameworkCore;
using Exvo.CheckInService.Models;

namespace Exvo.CheckInService.Data;

public class CheckInDbContext : DbContext
{
    public CheckInDbContext(DbContextOptions<CheckInDbContext> options) : base(options)
    {
    }

    public DbSet<ScannedTicket> ScannedTickets { get; set; } = null!;
    public DbSet<CheckInOutboxMessage> OutboxMessages { get; set; } = null!;
    public DbSet<CheckInAuditLog> AuditLogs { get; set; } = null!;

    // Kept for backward compatibility if needed
    public DbSet<CoordinatorAssignment> CoordinatorAssignments { get; set; } = null!;
    public DbSet<CheckInRecord> CheckInRecords { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ScannedTicket>(entity =>
        {
            entity.HasKey(t => t.Id);

            // A ticket can only have one successful CheckedIn record / unique ticket scan constraint
            entity.HasIndex(t => t.TicketId)
                  .IsUnique()
                  .HasDatabaseName("IX_ScannedTickets_TicketId");

            entity.HasIndex(t => t.EventId)
                  .HasDatabaseName("IX_ScannedTickets_EventId");

            entity.HasIndex(t => new { t.EventId, t.Status })
                  .HasDatabaseName("IX_ScannedTickets_EventId_Status");

            entity.HasIndex(t => t.QrCodeHash)
                  .HasDatabaseName("IX_ScannedTickets_QrCodeHash");

            entity.Property(t => t.Status)
                  .HasConversion<string>()
                  .HasMaxLength(20)
                  .IsRequired();

            entity.Property(t => t.QrCodeHash)
                  .HasMaxLength(128)
                  .IsRequired();

            entity.Property(t => t.AttendeeName).HasMaxLength(200);
            entity.Property(t => t.TicketType).HasMaxLength(100);
            entity.Property(t => t.SeatCode).HasMaxLength(50);
            entity.Property(t => t.RejectionReason).HasMaxLength(500);
        });

        modelBuilder.Entity<CheckInOutboxMessage>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Topic).HasMaxLength(100).IsRequired();
            entity.Property(m => m.PartitionKey).HasMaxLength(100).IsRequired();
            entity.Property(m => m.Payload).IsRequired();

            entity.HasIndex(m => m.ProcessedAtUtc)
                  .HasDatabaseName("IX_OutboxMessages_ProcessedAt");
        });

        modelBuilder.Entity<CheckInAuditLog>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Action).HasMaxLength(100).IsRequired();
            entity.Property(l => l.Details).HasMaxLength(1000);
            entity.HasIndex(l => l.EventId).HasDatabaseName("IX_AuditLogs_EventId");
        });

        modelBuilder.Entity<CoordinatorAssignment>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Status)
                  .HasConversion<string>()
                  .HasMaxLength(20)
                  .IsRequired();

            entity.HasIndex(a => new { a.CoordinatorUserId, a.EventId })
                  .IsUnique()
                  .HasDatabaseName("IX_CoordinatorAssignment_User_Event");
        });

        modelBuilder.Entity<CheckInRecord>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.TicketCode).HasMaxLength(100).IsRequired();
            entity.HasIndex(r => new { r.TicketCode, r.EventId })
                  .IsUnique()
                  .HasDatabaseName("IX_CheckInRecord_TicketCode_Event");
        });
    }
}
