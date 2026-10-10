namespace Exvo.CheckInService.Models;

/// <summary>
/// Represents an assignment of a coordinator (user) to a specific event,
/// granting them scanner access for that event.
/// </summary>
public class CoordinatorAssignment
{
    public int Id { get; set; }

    /// <summary>The user ID of the coordinator (from Auth service).</summary>
    public int CoordinatorUserId { get; set; }

    /// <summary>The event ID (from Catalog service).</summary>
    public int EventId { get; set; }

    /// <summary>Assignment status: Active or Revoked.</summary>
    public AssignmentStatus Status { get; set; } = AssignmentStatus.Active;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAtUtc { get; set; }
}

public enum AssignmentStatus
{
    Active = 0,
    Revoked = 1
}
