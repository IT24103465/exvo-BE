using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Exvo.CatalogService.Migrations
{
    /// <inheritdoc />
    public partial class AddArtistOrOrganizerToEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArtistOrOrganizer",
                table: "Events",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArtistOrOrganizer",
                table: "Events");
        }
    }
}
