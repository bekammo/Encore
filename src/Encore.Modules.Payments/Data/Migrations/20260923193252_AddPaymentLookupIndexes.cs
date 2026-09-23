using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Encore.Modules.Payments.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentLookupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_payments_order",
                schema: "payments",
                table: "payments",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "ix_payments_pending",
                schema: "payments",
                table: "payments",
                column: "AttemptedAt",
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "ix_payments_timed_out",
                schema: "payments",
                table: "payments",
                column: "ResolvedAt",
                filter: "\"Status\" = 4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payments_order",
                schema: "payments",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "ix_payments_pending",
                schema: "payments",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "ix_payments_timed_out",
                schema: "payments",
                table: "payments");
        }
    }
}
