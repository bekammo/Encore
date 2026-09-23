using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Encore.Modules.Orders.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderSoldAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SoldAt",
                schema: "orders",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            // Orders already awaiting capture have no recorded sale time. Placement is the
            // nearest honest one, and it puts them in front of the capture sweep.
            migrationBuilder.Sql(
                """UPDATE orders.orders SET "SoldAt" = "PlacedAt" WHERE "Status" = 5;""");

            migrationBuilder.CreateIndex(
                name: "ix_orders_awaiting_capture",
                schema: "orders",
                table: "orders",
                column: "SoldAt",
                filter: "\"Status\" = 5");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_orders_awaiting_capture",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "SoldAt",
                schema: "orders",
                table: "orders");
        }
    }
}
