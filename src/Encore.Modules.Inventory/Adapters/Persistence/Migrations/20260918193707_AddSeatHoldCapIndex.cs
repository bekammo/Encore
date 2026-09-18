using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Encore.Modules.Inventory.Adapters.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSeatHoldCapIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_seats_event_client_status",
                schema: "inventory",
                table: "seats",
                columns: new[] { "EventId", "HeldByClientId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_seats_event_client_status",
                schema: "inventory",
                table: "seats");
        }
    }
}
