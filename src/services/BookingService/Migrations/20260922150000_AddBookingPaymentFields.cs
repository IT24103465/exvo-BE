using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Exvo.BookingService.Migrations;

[Migration("20260922150000_AddBookingPaymentFields")]
public partial class AddBookingPaymentFields : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE Bookings ADD COLUMN IF NOT EXISTS PaymentMethod varchar(30) NOT NULL DEFAULT 'OnlineTransfer';");
        migrationBuilder.Sql("ALTER TABLE Bookings ADD COLUMN IF NOT EXISTS PaymentStatus varchar(20) NOT NULL DEFAULT 'Pending';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PaymentMethod", table: "Bookings");
        migrationBuilder.DropColumn(name: "PaymentStatus", table: "Bookings");
    }
}