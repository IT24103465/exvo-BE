namespace Exvo.BookingService.Contracts;

public record SeatingPlanRequest(string Name, bool IsVisibleToAttendees, string? Status, List<SeatingSectionRequest> Sections);
public record SeatingSectionRequest(int? Id, string Name, int RowCount, int SeatsPerRow, string StartingRowLabel, int StartingSeatNumber, int? TicketTierId, decimal Price, int DisplayOrder, List<string>? DisabledSeatCodes = null);

public record SeatingPlanResponse(int Id, int EventId, string Name, bool IsVisibleToAttendees, string Status, int Version, List<SeatingSectionResponse> Sections);
public record SeatingSectionResponse(int Id, string Name, int RowCount, int SeatsPerRow, string StartingRowLabel, int StartingSeatNumber, int? TicketTierId, decimal Price, int DisplayOrder, List<SeatResponse> Seats);
public record SeatResponse(string SeatCode, string RowLabel, int SeatNumber, int? TicketTierId, decimal Price, bool IsEnabled, string Status);
public record SeatingAvailabilityResponse(int EventId, int AvailableSeatCount, int TotalSeatCount, int HeldSeatCount, int BookedSeatCount, string InventoryStatus, List<TierAvailabilityResponse> Tiers);
public record TierAvailabilityResponse(int? TicketTierId, decimal Price, int AvailableQuantity, int TotalQuantity, int HeldQuantity, int BookedQuantity);
public record SeatHoldRequest(List<string> SeatCodes);
public record SeatHoldResponse(int HoldId, int EventId, List<string> SeatCodes, DateTime ExpiresAtUtc);
public record ConfirmSeatHoldRequest(string? PaymentMethod = null, List<GeneralTicketSelectionRequest>? Tickets = null);
public record GeneralBookingRequest(List<GeneralTicketSelectionRequest> Tickets);
public record GeneralTicketSelectionRequest(int? TicketTierId, string Name, decimal UnitPrice, int Quantity);
public record BookingConfirmationResponse(int BookingId, string BookingReference, int EventId, decimal TotalAmount, string Currency, List<string> SeatCodes, List<ConfirmedTicketSelectionResponse> Tickets);
public record ConfirmedTicketSelectionResponse(int? TicketTierId, string Name, decimal UnitPrice, int Quantity);
public record AttendeeBookingResponse(int BookingId, string BookingReference, int EventId, string Status, decimal TotalAmount, string Currency, DateTime CreatedAtUtc, DateTime? ConfirmedAtUtc, List<AttendeeTicketResponse> Tickets);
public record AttendeeTicketResponse(int BookingItemId, string TicketCode, string SeatCode, string RowLabel, int SeatNumber, string SectionName, int? TicketTierId, decimal Price, bool HasSeat);
