using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Exvo.BookingService.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingInventoryLocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BookingItems_Seats_SeatId",
                table: "BookingItems");

            migrationBuilder.DropIndex(
                name: "IX_BookingItems_SeatId",
                table: "BookingItems");

            migrationBuilder.CreateTable(
                name: "BookingInventoryLocks",
                columns: table => new
                {
                    EventId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingInventoryLocks", x => x.EventId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_BookingItems_SeatId",
                table: "BookingItems",
                column: "SeatId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BookingItems_Seats_SeatId",
                table: "BookingItems",
                column: "SeatId",
                principalTable: "Seats",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BookingItems_Seats_SeatId",
                table: "BookingItems");

            migrationBuilder.DropTable(
                name: "BookingInventoryLocks");

            migrationBuilder.DropIndex(
                name: "IX_BookingItems_SeatId",
                table: "BookingItems");

            migrationBuilder.CreateIndex(
                name: "IX_BookingItems_SeatId",
                table: "BookingItems",
                column: "SeatId");

            migrationBuilder.AddForeignKey(
                name: "FK_BookingItems_Seats_SeatId",
                table: "BookingItems",
                column: "SeatId",
                principalTable: "Seats",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
