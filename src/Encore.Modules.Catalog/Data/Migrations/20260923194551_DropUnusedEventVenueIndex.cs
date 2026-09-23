using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Encore.Modules.Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropUnusedEventVenueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_events_venue",
                schema: "catalog",
                table: "events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_events_venue",
                schema: "catalog",
                table: "events",
                column: "VenueId");
        }
    }
}
