using System.Globalization;
using System.Text;
using Exvo.CheckInService.Models.DTOs;

namespace Exvo.CheckInService.Services;

public static class PdfReportGenerator
{
    public static byte[] GenerateSalesReportPdf(EventSalesReportResponse report)
    {
        var builder = new SimplePdfBuilder();

        // 1. Top Header Banner
        builder.DrawRect(0, 775, 595.28, 67, 0.08, 0.18, 0.36, fill: true, stroke: false);
        builder.DrawText(40, 815, "EXVO - SALES & REVENUE REPORT", 18, isBold: true, 1.0, 1.0, 1.0);
        builder.DrawText(40, 795, "Dynamic Backend Sales & Inventory Intelligence", 9, isBold: false, 0.85, 0.9, 1.0);

        // 2. Event Metadata Box
        builder.DrawRect(40, 690, 515, 75, 0.96, 0.97, 0.99, fill: true, stroke: true, 0.82, 0.85, 0.9);
        builder.DrawText(50, 748, $"Event: {Sanitize(report.Title)} (ID: #{report.EventId})", 11, isBold: true, 0.1, 0.15, 0.25);
        builder.DrawText(50, 733, $"Date: {report.EventDate:yyyy-MM-dd HH:mm UTC}   |   Venue: {Sanitize(report.Venue)}", 9, isBold: false, 0.2, 0.2, 0.2);
        builder.DrawText(50, 719, $"Location: {Sanitize(report.Location)}   |   Organizer: {Sanitize(report.OrganizerName)}", 9, isBold: false, 0.2, 0.2, 0.2);
        builder.DrawText(50, 703, $"Currency: {Sanitize(report.Currency)}   |   Generated: {report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss UTC}", 9, isBold: false, 0.35, 0.35, 0.35);

        // 3. KPI Summary Metric Cards (4 Cards)
        // Card 1: Net Revenue
        DrawMetricCard(builder, 40, 615, 120, 60, "NET REVENUE", $"{report.TotalRevenue:N2} {report.Currency}", 0.1, 0.55, 0.2);
        // Card 2: Tickets Sold
        DrawMetricCard(builder, 170, 615, 120, 60, "TICKETS SOLD", $"{report.TotalSoldTickets} / {report.TotalCapacity}", 0.08, 0.35, 0.7);
        // Card 3: Orders Confirmed
        DrawMetricCard(builder, 300, 615, 120, 60, "CONFIRMED ORDERS", $"{report.ConfirmedBookingsCount}", 0.2, 0.2, 0.3);
        // Card 4: Refunds & Cancellations
        DrawMetricCard(builder, 430, 615, 125, 60, "CANCELLED / REFUNDS", $"{report.CancelledBookingsCount} ({report.RefundedAmount:N2})", 0.75, 0.2, 0.2);

        // 4. Ticket Tier Sales Breakdown Table
        var y = 580.0;
        builder.DrawText(40, y, "TICKET TIER SALES BREAKDOWN", 12, isBold: true, 0.1, 0.15, 0.25);
        y -= 18;

        // Table Header
        builder.DrawRect(40, y - 4, 515, 18, 0.12, 0.22, 0.42, fill: true, stroke: false);
        builder.DrawText(45, y, "Tier Name", 9, isBold: true, 1.0, 1.0, 1.0);
        builder.DrawText(190, y, $"Price ({report.Currency})", 9, isBold: true, 1.0, 1.0, 1.0);
        builder.DrawText(275, y, "Sold", 9, isBold: true, 1.0, 1.0, 1.0);
        builder.DrawText(330, y, "Remaining", 9, isBold: true, 1.0, 1.0, 1.0);
        builder.DrawText(410, y, $"Revenue ({report.Currency})", 9, isBold: true, 1.0, 1.0, 1.0);
        builder.DrawText(505, y, "Share", 9, isBold: true, 1.0, 1.0, 1.0);
        y -= 18;

        if (report.TierSales != null && report.TierSales.Count > 0)
        {
            var isAlt = false;
            foreach (var tier in report.TierSales)
            {
                if (isAlt)
                {
                    builder.DrawRect(40, y - 4, 515, 17, 0.95, 0.96, 0.98, fill: true, stroke: false);
                }

                var sharePct = report.TotalSoldTickets > 0
                    ? Math.Round((double)tier.SoldQuantity / report.TotalSoldTickets * 100.0, 1)
                    : 0.0;

                builder.DrawText(45, y, Sanitize(tier.TierName), 9, isBold: false, 0.15, 0.15, 0.15);
                builder.DrawText(190, y, tier.UnitPrice.ToString("N2", CultureInfo.InvariantCulture), 9, isBold: false, 0.15, 0.15, 0.15);
                builder.DrawText(275, y, tier.SoldQuantity.ToString(), 9, isBold: false, 0.15, 0.15, 0.15);
                builder.DrawText(330, y, tier.RemainingQuantity.ToString(), 9, isBold: false, 0.15, 0.15, 0.15);
                builder.DrawText(410, y, tier.Revenue.ToString("N2", CultureInfo.InvariantCulture), 9, isBold: false, 0.15, 0.15, 0.15);
                builder.DrawText(505, y, $"{sharePct:F1}%", 9, isBold: false, 0.15, 0.15, 0.15);

                y -= 17;
                isAlt = !isAlt;
            }
        }
        else
        {
            builder.DrawText(45, y, "General Admission", 9, isBold: false, 0.15, 0.15, 0.15);
            builder.DrawText(190, y, "0.00", 9, isBold: false, 0.15, 0.15, 0.15);
            builder.DrawText(275, y, report.TotalSoldTickets.ToString(), 9, isBold: false, 0.15, 0.15, 0.15);
            builder.DrawText(330, y, report.RemainingTickets.ToString(), 9, isBold: false, 0.15, 0.15, 0.15);
            builder.DrawText(410, y, report.TotalRevenue.ToString("N2", CultureInfo.InvariantCulture), 9, isBold: false, 0.15, 0.15, 0.15);
            builder.DrawText(505, y, "100.0%", 9, isBold: false, 0.15, 0.15, 0.15);
            y -= 17;
        }

        // Total Summary Row
        builder.DrawRect(40, y - 4, 515, 18, 0.9, 0.92, 0.96, fill: true, stroke: true, 0.7, 0.75, 0.85);
        builder.DrawText(45, y, "TOTAL", 9, isBold: true, 0.1, 0.15, 0.25);
        builder.DrawText(275, y, report.TotalSoldTickets.ToString(), 9, isBold: true, 0.1, 0.15, 0.25);
        builder.DrawText(330, y, report.RemainingTickets.ToString(), 9, isBold: true, 0.1, 0.15, 0.25);
        builder.DrawText(410, y, report.TotalRevenue.ToString("N2", CultureInfo.InvariantCulture), 9, isBold: true, 0.1, 0.15, 0.25);
        builder.DrawText(505, y, "100.0%", 9, isBold: true, 0.1, 0.15, 0.25);

        // 5. Footer
        DrawFooter(builder);

        return builder.Build();
    }

    public static byte[] GenerateAttendanceReportPdf(EventAttendanceReportResponse report)
    {
        var builder = new SimplePdfBuilder();

        // 1. Top Header Banner
        builder.DrawRect(0, 775, 595.28, 67, 0.12, 0.3, 0.24, fill: true, stroke: false);
        builder.DrawText(40, 815, "EXVO - ATTENDANCE & CHECK-IN REPORT", 18, isBold: true, 1.0, 1.0, 1.0);
        builder.DrawText(40, 795, "Real-Time Event Attendance & Admission Intelligence", 9, isBold: false, 0.9, 1.0, 0.95);

        // 2. Event Metadata Box
        builder.DrawRect(40, 690, 515, 75, 0.96, 0.98, 0.96, fill: true, stroke: true, 0.8, 0.88, 0.82);
        builder.DrawText(50, 748, $"Event: {Sanitize(report.Title)} (ID: #{report.EventId})", 11, isBold: true, 0.1, 0.25, 0.15);
        builder.DrawText(50, 733, $"Date: {report.EventDate:yyyy-MM-dd HH:mm UTC}   |   Venue: {Sanitize(report.Venue)}", 9, isBold: false, 0.2, 0.2, 0.2);
        builder.DrawText(50, 719, $"Location: {Sanitize(report.Location)}   |   Organizer: {Sanitize(report.OrganizerName)}", 9, isBold: false, 0.2, 0.2, 0.2);
        builder.DrawText(50, 703, $"Generated At: {report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss UTC}", 9, isBold: false, 0.35, 0.35, 0.35);

        // 3. KPI Summary Metric Cards (4 Cards)
        DrawMetricCard(builder, 40, 615, 120, 60, "EXPECTED ATTENDEES", $"{report.TotalSoldTickets}", 0.08, 0.35, 0.7);
        DrawMetricCard(builder, 170, 615, 120, 60, "UNIQUE CHECKED IN", $"{report.TotalUniqueCheckedIn}", 0.1, 0.55, 0.2);
        DrawMetricCard(builder, 300, 615, 120, 60, "CHECK-IN RATE", $"{report.CheckInRate:F1}%", 0.8, 0.45, 0.1);
        DrawMetricCard(builder, 430, 615, 125, 60, "OCCUPANCY RATE", $"{report.OccupancyPercentage:F1}%", 0.5, 0.2, 0.6);

        // 4. Tier Attendance Table
        var y = 580.0;
        builder.DrawText(40, y, "TIER ATTENDANCE BREAKDOWN", 12, isBold: true, 0.1, 0.25, 0.15);
        y -= 18;

        builder.DrawRect(40, y - 4, 515, 18, 0.12, 0.3, 0.24, fill: true, stroke: false);
        builder.DrawText(45, y, "Ticket Type", 9, isBold: true, 1.0, 1.0, 1.0);
        builder.DrawText(220, y, "Sold (Expected)", 9, isBold: true, 1.0, 1.0, 1.0);
        builder.DrawText(340, y, "Checked In", 9, isBold: true, 1.0, 1.0, 1.0);
        builder.DrawText(450, y, "Attendance Rate", 9, isBold: true, 1.0, 1.0, 1.0);
        y -= 18;

        if (report.TierAttendance != null && report.TierAttendance.Count > 0)
        {
            var isAlt = false;
            foreach (var tier in report.TierAttendance)
            {
                if (isAlt)
                {
                    builder.DrawRect(40, y - 4, 515, 17, 0.95, 0.97, 0.95, fill: true, stroke: false);
                }

                builder.DrawText(45, y, Sanitize(tier.TicketType), 9, isBold: false, 0.15, 0.15, 0.15);
                builder.DrawText(220, y, tier.TotalSold.ToString(), 9, isBold: false, 0.15, 0.15, 0.15);
                builder.DrawText(340, y, tier.CheckedInCount.ToString(), 9, isBold: false, 0.15, 0.15, 0.15);
                builder.DrawText(450, y, $"{tier.AttendancePercentage:F1}%", 9, isBold: false, 0.15, 0.15, 0.15);

                y -= 17;
                isAlt = !isAlt;
            }
        }
        else
        {
            builder.DrawText(45, y, "General Admission", 9, isBold: false, 0.15, 0.15, 0.15);
            builder.DrawText(220, y, report.TotalSoldTickets.ToString(), 9, isBold: false, 0.15, 0.15, 0.15);
            builder.DrawText(340, y, report.TotalUniqueCheckedIn.ToString(), 9, isBold: false, 0.15, 0.15, 0.15);
            builder.DrawText(450, y, $"{report.CheckInRate:F1}%", 9, isBold: false, 0.15, 0.15, 0.15);
            y -= 17;
        }

        // Summary Totals Row
        builder.DrawRect(40, y - 4, 515, 18, 0.9, 0.95, 0.92, fill: true, stroke: true, 0.75, 0.85, 0.78);
        builder.DrawText(45, y, "TOTAL", 9, isBold: true, 0.1, 0.25, 0.15);
        builder.DrawText(220, y, report.TotalSoldTickets.ToString(), 9, isBold: true, 0.1, 0.25, 0.15);
        builder.DrawText(340, y, report.TotalUniqueCheckedIn.ToString(), 9, isBold: true, 0.1, 0.25, 0.15);
        builder.DrawText(450, y, $"{report.CheckInRate:F1}%", 9, isBold: true, 0.1, 0.25, 0.15);
        y -= 25;

        // 5. Hourly Check-In Timeline (if available)
        if (report.HourlyCheckIns != null && report.HourlyCheckIns.Count > 0)
        {
            builder.DrawText(40, y, "HOURLY CHECK-IN TIMELINE", 11, isBold: true, 0.1, 0.25, 0.15);
            y -= 16;

            builder.DrawRect(40, y - 4, 515, 16, 0.2, 0.4, 0.3, fill: true, stroke: false);
            builder.DrawText(45, y, "Hour (UTC)", 8, isBold: true, 1.0, 1.0, 1.0);
            builder.DrawText(300, y, "Unique Check-Ins Recorded", 8, isBold: true, 1.0, 1.0, 1.0);
            y -= 15;

            foreach (var hourly in report.HourlyCheckIns.Take(6))
            {
                builder.DrawText(45, y, hourly.HourUtc.ToString("yyyy-MM-dd HH:mm"), 8, isBold: false, 0.2, 0.2, 0.2);
                builder.DrawText(300, y, hourly.CheckInCount.ToString(), 8, isBold: false, 0.2, 0.2, 0.2);
                y -= 14;
            }
            y -= 10;
        }

        // 6. Exit Tracking Disclaimer Notice Box
        builder.DrawRect(40, y - 10, 515, 34, 0.99, 0.97, 0.92, fill: true, stroke: true, 0.92, 0.85, 0.7);
        builder.DrawText(50, y + 10, "NOTE ON PHYSICAL OCCUPANCY:", 8, isBold: true, 0.5, 0.35, 0.1);
        builder.DrawText(50, y - 2, Sanitize(report.OccupancyNote), 8, isBold: false, 0.35, 0.3, 0.2);

        // 7. Footer
        DrawFooter(builder);

        return builder.Build();
    }

    private static void DrawMetricCard(SimplePdfBuilder b, double x, double y, double w, double h, string label, string value, double r, double g, double bl)
    {
        b.DrawRect(x, y, w, h, 0.97, 0.98, 1.0, fill: true, stroke: true, 0.85, 0.88, 0.92);
        // Accent bar on top of card
        b.DrawRect(x, y + h - 3, w, 3, r, g, bl, fill: true, stroke: false);
        b.DrawText(x + 8, y + 36, label, 8, isBold: true, 0.45, 0.45, 0.5);
        b.DrawText(x + 8, y + 16, Sanitize(value), 12, isBold: true, r, g, bl);
    }

    private static void DrawFooter(SimplePdfBuilder b)
    {
        b.DrawLine(40, 48, 555, 48, 0.8, 0.8, 0.8);
        b.DrawText(40, 34, "EXVO Event Management Platform - Automated Intelligence Engine", 8, isBold: false, 0.5, 0.5, 0.5);
        b.DrawText(505, 34, "Page 1 of 1", 8, isBold: false, 0.5, 0.5, 0.5);
    }

    private static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch < 128)
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append(' ');
            }
        }
        return sb.ToString();
    }

    private sealed class SimplePdfBuilder
    {
        private readonly StringBuilder _stream = new();

        public void DrawRect(double x, double y, double w, double h, double r, double g, double b, bool fill, bool stroke, double sr = 0, double sg = 0, double sb = 0)
        {
            _stream.Append(CultureInfo.InvariantCulture, $"{r:F3} {g:F3} {b:F3} rg\n");
            if (stroke)
            {
                _stream.Append(CultureInfo.InvariantCulture, $"{sr:F3} {sg:F3} {sb:F3} RG 0.75 w\n");
            }
            _stream.Append(CultureInfo.InvariantCulture, $"{x:F2} {y:F2} {w:F2} {h:F2} re ");
            if (fill && stroke) _stream.Append("B\n");
            else if (fill) _stream.Append("f\n");
            else if (stroke) _stream.Append("S\n");
            else _stream.Append("n\n");
        }

        public void DrawLine(double x1, double y1, double x2, double y2, double r, double g, double b)
        {
            _stream.Append(CultureInfo.InvariantCulture, $"{r:F3} {g:F3} {b:F3} RG 0.75 w\n");
            _stream.Append(CultureInfo.InvariantCulture, $"{x1:F2} {y1:F2} m {x2:F2} {y2:F2} l S\n");
        }

        public void DrawText(double x, double y, string text, double fontSize, bool isBold, double r, double g, double b)
        {
            var font = isBold ? "/F2" : "/F1";
            var escaped = text
                .Replace("\\", "\\\\")
                .Replace("(", "\\(")
                .Replace(")", "\\)");

            _stream.Append(CultureInfo.InvariantCulture, $"BT {font} {fontSize:F1} Tf {r:F3} {g:F3} {b:F3} rg {x:F2} {y:F2} Td ({escaped}) Tj ET\n");
        }

        public byte[] Build()
        {
            var contentStream = Encoding.ASCII.GetBytes(_stream.ToString());
            var streamLen = contentStream.Length;

            var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true);

            var offsets = new List<long>();

            writer.Write("%PDF-1.4\n");
            writer.Flush();

            // Object 1: Catalog
            offsets.Add(ms.Position);
            writer.Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            writer.Flush();

            // Object 2: Pages
            offsets.Add(ms.Position);
            writer.Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            writer.Flush();

            // Object 3: Page
            offsets.Add(ms.Position);
            writer.Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.28 841.89] /Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>\nendobj\n");
            writer.Flush();

            // Object 4: Font F1 (Helvetica)
            offsets.Add(ms.Position);
            writer.Write("4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
            writer.Flush();

            // Object 5: Font F2 (Helvetica-Bold)
            offsets.Add(ms.Position);
            writer.Write("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");
            writer.Flush();

            // Object 6: Content Stream
            offsets.Add(ms.Position);
            writer.Write($"6 0 obj\n<< /Length {streamLen} >>\nstream\n");
            writer.Flush();
            ms.Write(contentStream, 0, contentStream.Length);
            writer.Write("\nendstream\nendobj\n");
            writer.Flush();

            // Xref table
            var startXref = ms.Position;
            writer.Write("xref\n");
            writer.Write($"0 {offsets.Count + 1}\n");
            writer.Write("0000000000 65535 f \n");
            foreach (var off in offsets)
            {
                writer.Write($"{off:D10} 00000 n \n");
            }

            // Trailer
            writer.Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{startXref}\n%%EOF\n");
            writer.Flush();

            return ms.ToArray();
        }
    }
}
