using Exvo.BookingService.Data;
using Exvo.BookingService.Models;
using Microsoft.EntityFrameworkCore;

namespace Exvo.BookingService.Services;

public static class SeatGenerator
{
    public static async Task RegenerateAsync(BookingDbContext db, SeatingPlan plan, ISet<int>? activeSectionIds = null, CancellationToken cancellationToken = default)
    {
        var sections = plan.Sections
            .Where(section => activeSectionIds is null || activeSectionIds.Contains(section.Id))
            .OrderBy(section => section.DisplayOrder)
            .ToList();
        var existingSeats = await db.Seats.Where(seat => seat.SeatingPlanId == plan.Id).ToListAsync(cancellationToken);
        var existingSeatIds = existingSeats.Select(seat => seat.Id).ToArray();
        var protectedSeatIds = (await db.BookingItems.AsNoTracking()
                .Where(item => item.SeatId.HasValue && existingSeatIds.Contains(item.SeatId.Value))
                .Select(item => item.SeatId!.Value)
                .Concat(db.SeatHoldItems.AsNoTracking()
                    .Where(item => existingSeatIds.Contains(item.SeatId))
                    .Select(item => item.SeatId))
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var desiredCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in sections)
        {
            for (var rowIndex = 0; rowIndex < section.RowCount; rowIndex++)
            {
                var rowLabel = IncrementRowLabel(section.StartingRowLabel, rowIndex);
                for (var seatIndex = 0; seatIndex < section.SeatsPerRow; seatIndex++)
                {
                    var seatNumber = section.StartingSeatNumber + seatIndex;
                    var code = $"{rowLabel}-{seatNumber:00}";
                    desiredCodes.Add(code);
                    var seat = existingSeats.FirstOrDefault(item => item.SeatCode == code);
                    if (seat is null)
                    {
                        db.Seats.Add(new Seat
                        {
                            SeatingPlanId = plan.Id,
                            SeatingSectionId = section.Id,
                            SeatCode = code,
                            RowLabel = rowLabel,
                            SeatNumber = seatNumber,
                            TicketTierId = section.TicketTierId,
                            Price = section.Price,
                            IsEnabled = true,
                            Status = SeatStatus.Available,
                            CreatedAtUtc = DateTime.UtcNow,
                            UpdatedAtUtc = DateTime.UtcNow
                        });
                    }
                    else
                    {
                        seat.SeatingSectionId = section.Id;
                        seat.RowLabel = rowLabel;
                        seat.SeatNumber = seatNumber;
                        seat.TicketTierId = section.TicketTierId;
                        seat.Price = section.Price;
                        seat.IsEnabled = true;
                        seat.UpdatedAtUtc = DateTime.UtcNow;
                        seat.Version++;
                    }
                }
            }
        }

        foreach (var seat in existingSeats.Where(seat => !desiredCodes.Contains(seat.SeatCode)))
        {
            if (seat.Status is SeatStatus.Held or SeatStatus.Booked || protectedSeatIds.Contains(seat.Id))
            {
                seat.IsEnabled = false;
                seat.UpdatedAtUtc = DateTime.UtcNow;
                seat.Version++;
            }
            else
            {
                db.Seats.Remove(seat);
            }
        }
    }

    private static string IncrementRowLabel(string startingLabel, int offset)
    {
        var normalized = string.IsNullOrWhiteSpace(startingLabel) ? "A" : startingLabel.Trim().ToUpperInvariant();
        var value = 0;
        foreach (var character in normalized)
        {
            value = value * 26 + character - 'A' + 1;
        }
        value += offset;
        var result = string.Empty;
        while (value > 0)
        {
            value--;
            result = (char)('A' + value % 26) + result;
            value /= 26;
        }
        return result;
    }
}
