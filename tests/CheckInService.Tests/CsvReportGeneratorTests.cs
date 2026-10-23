using System.Globalization;
using System.Text;
using Exvo.CheckInService.Models.DTOs;
using Exvo.CheckInService.Services;

namespace Exvo.CheckInService.Tests;

public class CsvReportGeneratorTests
{
    private static readonly DateTime Date = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static EventSalesReportResponse Sales() => new(7, "Music, \"Live\"", Date, "Hall", "Colombo", "Org",
        100, 20, 80, 1200m, 1100m, "LKR", 12, 3, 2, 200m,
        [new(5, "VIP", 150m, 5, 25, 20, 750m), new(null, "Standard", 30m, 15, 75, 60, 450m)], Date);

    private static EventAttendanceReportResponse Attendance() => new(7, "Music, \"Live\"", Date, "Hall", "Colombo", "Org",
        100, 20, 15, 5, 75, 15, 3, false, "Entry counts only",
        [new("VIP", 10, 7, 70)], [new(Date, 15)], Date);

    private static string Decode(byte[] bytes)
    {
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Replace("\r\n", "\n");
    }

    [Fact]
    public void Sales_csv_exports_metadata_totals_tiers_and_escaped_text()
    {
        using var culture = new InvariantCultureScope();
        var csv = Decode(CsvReportGenerator.GenerateSalesReportCsv(Sales()));
        foreach (var row in new[]
        {
            "EXVO SALES & REVENUE REPORT",
            "Generated At (UTC),Event ID,Title,Event Date (UTC),Venue,Location,Organizer,Currency",
            "\nKEY SALES METRICS\nMetric,Value",
            "\nTICKET TIER SALES BREAKDOWN",
            "Tier ID,Tier Name,Unit Price (LKR),Sold Quantity,Total Capacity,Remaining Quantity,Revenue (LKR),Share of Sold Units (%)",
            "\"2026-10-23 12:00:00Z\",7,\"Music, \"\"Live\"\"\",\"2026-10-23 12:00:00Z\",\"Hall\",\"Colombo\",\"Org\",\"LKR\"",
            "Total Tickets Sold,20", "Total Capacity,100", "Remaining Available Tickets,80",
            "Confirmed Orders Count,12", "Cancelled Orders Count,3", "Refunded/Cancelled Tickets Count,2",
            "Gross Sales Revenue (LKR),1400.00", "Refunded / Cancelled Amount (LKR),200.00", "Net Revenue (LKR),1200.00",
            "5,\"VIP\",150.00,5,25,20,750.00,25.00%",
            "N/A,\"Standard\",30.00,15,75,60,450.00,75.00%",
            "TOTAL,All Tiers,-,20,100,80,1200.00,100.00%"
        }) Assert.Contains(row + "\n", csv);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sales_without_tiers_uses_general_admission(bool missing)
    {
        using var culture = new InvariantCultureScope();
        var report = Sales() with { TierSales = missing ? null! : [] };
        var csv = Decode(CsvReportGenerator.GenerateSalesReportCsv(report));
        Assert.Contains("N/A,General Admission,0.00,20,100,80,1200.00,100.00%\n", csv);
        Assert.DoesNotContain("TOTAL,All Tiers", csv);
    }

    [Fact]
    public void Zero_sales_has_finite_zero_share_and_fractional_shares_are_rounded()
    {
        using var culture = new InvariantCultureScope();
        var report = Sales() with { TotalSoldTickets = 0 };
        Assert.Contains("750.00,0.00%", Decode(CsvReportGenerator.GenerateSalesReportCsv(report)));
        report = report with { TotalSoldTickets = 3, TierSales = [new(1, "One", 10, 1, 10, 9, 10)] };
        Assert.Contains("10.00,33.33%", Decode(CsvReportGenerator.GenerateSalesReportCsv(report)));
    }

    [Fact]
    public void Attendance_csv_exports_metrics_and_timeline()
    {
        using var culture = new InvariantCultureScope();
        var csv = Decode(CsvReportGenerator.GenerateAttendanceReportCsv(Attendance()));
        foreach (var row in new[]
        {
            "EXVO ATTENDANCE & CHECK-IN REPORT",
            "Generated At (UTC),Event ID,Title,Event Date (UTC),Venue,Location,Organizer",
            "\nKEY ATTENDANCE METRICS\nMetric,Value",
            "\nTIER ATTENDANCE BREAKDOWN\nTicket Type,Total Sold,Checked In,Attendance Rate (%)",
            "\nHOURLY CHECK-IN TIMELINE\nHour (UTC),Unique Check-In Count",
            "\"2026-10-23 12:00:00Z\",7,\"Music, \"\"Live\"\"\",\"2026-10-23 12:00:00Z\",\"Hall\",\"Colombo\",\"Org\"",
            "Total Expected Attendees (Sold Tickets),20", "Total Unique Checked-In Attendees,15",
            "Remaining Expected Attendees,5", "Check-In Rate (Checked-In / Sold),75.00%", "Venue Capacity,100",
            "Occupancy Percentage (Current Inside / Capacity),15.00%",
            "Rejected Scan Attempts (Invalid/Duplicate/Cancelled),3", "Exit Tracking Active,False",
            "Occupancy Basis,\"Entry counts only\"", "\"VIP\",10,7,70.00%", "2026-10-23 12:00,15"
        }) Assert.Contains(row + "\n", csv);
        Assert.DoesNotContain("No check-ins recorded", csv);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("東京\nFestival")]
    public void Text_fields_preserve_empty_and_unicode_values(string? title)
    {
        using var culture = new InvariantCultureScope();
        var csv = Decode(CsvReportGenerator.GenerateSalesReportCsv(Sales() with { Title = title! }));
        Assert.Contains($",7,\"{title}\",\"2026-10-23", csv);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_attendance_lists_emit_no_tiers_and_explicit_empty_timeline(bool missing)
    {
        using var culture = new InvariantCultureScope();
        var report = Attendance() with { TierAttendance = missing ? null! : [], HourlyCheckIns = missing ? null! : [] };
        var csv = Decode(CsvReportGenerator.GenerateAttendanceReportCsv(report));
        Assert.Contains("No check-ins recorded yet,0\n", csv);
        Assert.DoesNotContain("\"VIP\"", csv);
    }

    private sealed class InvariantCultureScope : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentCulture;
        public InvariantCultureScope() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        public void Dispose() => CultureInfo.CurrentCulture = previous;
    }
}
