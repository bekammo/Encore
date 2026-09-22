using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Encore.Modules.Payments.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGatewayLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gateway_ledger",
                schema: "payments",
                columns: table => new
                {
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gateway_ledger", x => x.IdempotencyKey);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gateway_ledger",
                schema: "payments");
        }
    }
}
