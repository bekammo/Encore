using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Encore.Modules.Notifications.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropUnusedNotificationClientIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notifications_client_occurred",
                schema: "notifications",
                table: "notifications");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_notifications_client_occurred",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "ClientId", "OccurredAt" },
                descending: new[] { false, true });
        }
    }
}
