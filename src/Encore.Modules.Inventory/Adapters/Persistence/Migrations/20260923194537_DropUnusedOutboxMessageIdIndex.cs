using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Encore.Modules.Inventory.Adapters.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropUnusedOutboxMessageIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_message_id",
                schema: "inventory",
                table: "outbox_messages");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_message_id",
                schema: "inventory",
                table: "outbox_messages",
                column: "MessageId");
        }
    }
}
