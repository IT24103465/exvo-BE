using System.Text;
using Exvo.CheckInService.Models.DTOs;

namespace Exvo.CheckInService.Services;

public static class CsvReportGenerator
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public static byte[] GenerateSalesReportCsv(EventSalesReportResponse report)
    {
        var sb = new StringBuilder();

        // 1. Report Title & Event Metadata
        sb.AppendLine("EXVO SALES & REVENUE REPORT");
        sb.AppendLine("Generated At (UTC),Event ID,Title,Event Date (UTC),Venue,Location,Organizer,Currency");
        sb.AppendLine($"{Escape(report.GeneratedAtUtc.ToString("u"))},{report.EventId},{Escape(report.Title)},{Escape(report.EventDate.ToString("u"))},{Escape(report.Venue)},{Escape(report.Location)},{Escape(report.OrganizerName)},{Escape(report.Currency)}");
        sb.AppendLine();

        // 2. High-Level Financial & Volume KPIs
        sb.AppendLine("KEY SALES METRICS");
        sb.AppendLine("Metric,Value");
        sb.AppendLine($"Total Tickets Sold,{report.TotalSoldTickets}");
        sb.AppendLine($"Total Capacity,{report.TotalCapacity}");
        sb.AppendLine($"Remaining Available Tickets,{report.RemainingTickets}");
        sb.AppendLine($"Confirmed Orders Count,{report.ConfirmedBookingsCount}");
        sb.AppendLine($"Cancelled Orders Count,{report.CancelledBookingsCount}");
        sb.AppendLine($"Refunded/Cancelled Tickets Count,{report.RefundedTicketsCount}");
        sb.AppendLine($"Gross Sales Revenue ({report.Currency}),{report.TotalRevenue + report.RefundedAmount:F2}");
        sb.AppendLine($"Refunded / Cancelled Amount ({report.Currency}),{report.RefundedAmount:F2}");
        sb.AppendLine($"Net Revenue ({report.Currency}),{report.TotalRevenue:F2}");
        sb.AppendLine();

        // 3. Ticket Tier / Type Breakdown
        sb.AppendLine("TICKET TIER SALES BREAKDOWN");
        sb.AppendLine($"Tier ID,Tier Name,Unit Price ({report.Currency}),Sold Quantity,Total Capacity,Remaining Quantity,Revenue ({report.Currency}),Share of Sold Units (%)");

        if (report.TierSales != null && report.TierSales.Count > 0)
        {
            foreach (var tier in report.TierSales)
            {
                var sharePct = report.TotalSoldTickets > 0
                    ? Math.Round((double)tier.SoldQuantity / report.TotalSoldTickets * 100.0, 2)
                    : 0.0;

                sb.AppendLine($"{tier.TicketTierId?.ToString() ?? "N/A"},{Escape(tier.TierName)},{tier.UnitPrice:F2},{tier.SoldQuantity},{tier.TotalCapacity},{tier.RemainingQuantity},{tier.Revenue:F2},{sharePct:F2}%");
            }

            // Summary Totals Row
            sb.AppendLine($"TOTAL,All Tiers,-,{report.TotalSoldTickets},{report.TotalCapacity},{report.RemainingTickets},{report.TotalRevenue:F2},100.00%");
        }
        else
        {
            sb.AppendLine($"N/A,General Admission,0.00,{report.TotalSoldTickets},{report.TotalCapacity},{report.RemainingTickets},{report.TotalRevenue:F2},100.00%");
        }

        var csvBytes = Encoding.UTF8.GetBytes(sb.ToString());
        return CombineWithBom(csvBytes);
    }

    public static byte[] GenerateAttendanceReportCsv(EventAttendanceReportResponse report)
    {
        var sb = new StringBuilder();

        // 1. Report Title & Event Metadata
        sb.AppendLine("EXVO ATTENDANCE & CHECK-IN REPORT");
        sb.AppendLine("Generated At (UTC),Event ID,Title,Event Date (UTC),Venue,Location,Organizer");
        sb.AppendLine($"{Escape(report.GeneratedAtUtc.ToString("u"))},{report.EventId},{Escape(report.Title)},{Escape(report.EventDate.ToString("u"))},{Escape(report.Venue)},{Escape(report.Location)},{Escape(report.OrganizerName)}");
        sb.AppendLine();

        // 2. High-Level Attendance KPIs
        sb.AppendLine("KEY ATTENDANCE METRICS");
        sb.AppendLine("Metric,Value");
        sb.AppendLine($"Total Expected Attendees (Sold Tickets),{report.TotalSoldTickets}");
        sb.AppendLine($"Total Unique Checked-In Attendees,{report.TotalUniqueCheckedIn}");
        sb.AppendLine($"Remaining Expected Attendees,{report.RemainingExpectedAttendees}");
        sb.AppendLine($"Check-In Rate (Checked-In / Sold),{report.CheckInRate:F2}%");
        sb.AppendLine($"Venue Capacity,{report.VenueCapacity}");
        sb.AppendLine($"Occupancy Percentage (Current Inside / Capacity),{report.OccupancyPercentage:F2}%");
        sb.AppendLine($"Rejected Scan Attempts (Invalid/Duplicate/Cancelled),{report.TotalRejectedScans}");
        sb.AppendLine($"Exit Tracking Active,{report.ExitTrackingActive}");
        sb.AppendLine($"Occupancy Basis,{Escape(report.OccupancyNote)}");
        sb.AppendLine();

        // 3. Tier Attendance Breakdown
        sb.AppendLine("TIER ATTENDANCE BREAKDOWN");
        sb.AppendLine("Ticket Type,Total Sold,Checked In,Attendance Rate (%)");

        if (report.TierAttendance != null && report.TierAttendance.Count > 0)
        {
            foreach (var tier in report.TierAttendance)
            {
                sb.AppendLine($"{Escape(tier.TicketType)},{tier.TotalSold},{tier.CheckedInCount},{tier.AttendancePercentage:F2}%");
            }
        }
        sb.AppendLine();

        // 4. Hourly Check-In Timeline
        sb.AppendLine("HOURLY CHECK-IN TIMELINE");
        sb.AppendLine("Hour (UTC),Unique Check-In Count");

        if (report.HourlyCheckIns != null && report.HourlyCheckIns.Count > 0)
        {
            foreach (var hourly in report.HourlyCheckIns)
            {
                sb.AppendLine($"{hourly.HourUtc:yyyy-MM-dd HH:mm},{hourly.CheckInCount}");
            }
        }
        else
        {
            sb.AppendLine("No check-ins recorded yet,0");
        }

        var csvBytes = Encoding.UTF8.GetBytes(sb.ToString());
        return CombineWithBom(csvBytes);
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static byte[] CombineWithBom(byte[] utf8Bytes)
    {
        var result = new byte[Utf8Bom.Length + utf8Bytes.Length];
        Buffer.BlockCopy(Utf8Bom, 0, result, 0, Utf8Bom.Length);
        Buffer.BlockCopy(utf8Bytes, 0, result, Utf8Bom.Length, utf8Bytes.Length);
        return result;
    }
}
