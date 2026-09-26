using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Exvo.BookingService.Migrations;

public partial class AllowGeneralAdmissionBookingItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE BookingItems MODIFY COLUMN SeatId int NULL;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE BookingItems MODIFY COLUMN SeatId int NOT NULL;");
    }
}
