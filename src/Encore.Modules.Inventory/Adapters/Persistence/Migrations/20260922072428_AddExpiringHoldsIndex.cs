using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Encore.Modules.Inventory.Adapters.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExpiringHoldsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_seats_expiring_holds",
                schema: "inventory",
                table: "seats",
                column: "HoldExpiresAt",
                filter: "\"Status\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_seats_expiring_holds",
                schema: "inventory",
                table: "seats");
        }
    }
}
