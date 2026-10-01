using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Exvo.BookingService.Migrations;

public partial class AddBookingAttendeeEmail : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AttendeeEmail",
            table: "Bookings",
            type: "varchar(320)",
            maxLength: 320,
            nullable: false,
            defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AttendeeEmail",
            table: "Bookings");
    }
}
