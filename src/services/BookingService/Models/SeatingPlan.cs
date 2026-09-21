namespace Exvo.BookingService.Models;

public enum SeatingPlanStatus
{
    Draft,
    Published
}

public enum SeatStatus
{
    Available,
    Held,
    Booked,
    Blocked
}

public enum BookingStatus
{
    Pending,
    Confirmed,
    Cancelled,
    Failed
}

public class SeatingPlan
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public int OrganizerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsVisibleToAttendees { get; set; }
    public SeatingPlanStatus Status { get; set; } = SeatingPlanStatus.Draft;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int Version { get; set; } = 1;
    public List<SeatingSection> Sections { get; set; } = [];
    public List<Seat> Seats { get; set; } = [];
}

public class SeatingSection
{
    public int Id { get; set; }
    public int SeatingPlanId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int RowCount { get; set; }
    public int SeatsPerRow { get; set; }
    public string StartingRowLabel { get; set; } = "A";
    public int StartingSeatNumber { get; set; } = 1;
    public int? TicketTierId { get; set; }
    public decimal Price { get; set; }
    public int DisplayOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public SeatingPlan SeatingPlan { get; set; } = null!;
    public List<Seat> Seats { get; set; } = [];
}

public class Seat
{
    public int Id { get; set; }
    public int SeatingPlanId { get; set; }
    public int SeatingSectionId { get; set; }
    public string SeatCode { get; set; } = string.Empty;
    public string RowLabel { get; set; } = string.Empty;
    public int SeatNumber { get; set; }
    public int? TicketTierId { get; set; }
    public decimal Price { get; set; }
    public bool IsEnabled { get; set; } = true;
    public SeatStatus Status { get; set; } = SeatStatus.Available;
    public int? CurrentHoldId { get; set; }
    public int? BookingItemId { get; set; }
    public DateTime? HoldExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int Version { get; set; } = 1;
    public SeatingPlan SeatingPlan { get; set; } = null!;
    public SeatingSection SeatingSection { get; set; } = null!;
}

public class Booking
{
    public int Id { get; set; }
    public string BookingReference { get; set; } = string.Empty;
    public int EventId { get; set; }
    public int AttendeeUserId { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.Pending;
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "LKR";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<BookingItem> Items { get; set; } = [];
}

public class BookingItem
{
    public int Id { get; set; }
    public int BookingId { get; set; }
    public int SeatId { get; set; }
    public string SeatCode { get; set; } = string.Empty;
    public int? TicketTierId { get; set; }
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; } = 1;
    public Booking Booking { get; set; } = null!;
    public Seat Seat { get; set; } = null!;
}
