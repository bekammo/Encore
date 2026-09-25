using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Encore.Modules.Orders.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingOrderExpiryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_orders_pending_holds_expire",
                schema: "orders",
                table: "orders",
                column: "HoldsExpireAt",
                filter: "\"Status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_orders_pending_holds_expire",
                schema: "orders",
                table: "orders");
        }
    }
}
