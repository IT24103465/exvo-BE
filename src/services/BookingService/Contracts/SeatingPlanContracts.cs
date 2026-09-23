namespace Exvo.BookingService.Contracts;

public record SeatingPlanRequest(string Name, bool IsVisibleToAttendees, string? Status, List<SeatingSectionRequest> Sections);
public record SeatingSectionRequest(int? Id, string Name, int RowCount, int SeatsPerRow, string StartingRowLabel, int StartingSeatNumber, int? TicketTierId, decimal Price, int DisplayOrder, List<string>? DisabledSeatCodes = null);

public record SeatingPlanResponse(int Id, int EventId, string Name, bool IsVisibleToAttendees, string Status, int Version, List<SeatingSectionResponse> Sections);
public record SeatingSectionResponse(int Id, string Name, int RowCount, int SeatsPerRow, string StartingRowLabel, int StartingSeatNumber, int? TicketTierId, decimal Price, int DisplayOrder, List<SeatResponse> Seats);
public record SeatResponse(string SeatCode, string RowLabel, int SeatNumber, int? TicketTierId, decimal Price, bool IsEnabled, string Status);
public record SeatingAvailabilityResponse(int EventId, int AvailableSeatCount, List<TierAvailabilityResponse> Tiers);
public record TierAvailabilityResponse(int? TicketTierId, decimal Price, int AvailableQuantity);
public record SeatHoldRequest(List<string> SeatCodes);
public record SeatHoldResponse(int HoldId, int EventId, List<string> SeatCodes, DateTime ExpiresAtUtc);
public record ConfirmSeatHoldRequest(string? PaymentMethod = null);
public record BookingConfirmationResponse(int BookingId, string BookingReference, int EventId, decimal TotalAmount, string Currency, List<string> SeatCodes);
