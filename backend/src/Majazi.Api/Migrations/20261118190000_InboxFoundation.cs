using Majazi.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Majazi.Api.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261118190000_InboxFoundation")]
public sealed class InboxFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "inbox_conversations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                ChannelConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                ContactId = table.Column<Guid>(type: "uuid", nullable: true),
                ExternalConversationId = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: true),
                Subject = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                AssignedToUserId = table.Column<Guid>(type: "uuid", nullable: true),
                LastMessageAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inbox_conversations", x => x.Id);
                table.ForeignKey("FK_inbox_conversations_organizations_OrganizationId", x => x.OrganizationId, "organizations", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_inbox_conversations_social_channel_connections_ChannelConnectionId", x => x.ChannelConnectionId, "social_channel_connections", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_inbox_conversations_crm_contacts_ContactId", x => x.ContactId, "crm_contacts", "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "inbox_messages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalMessageId = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: true),
                Direction = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                Body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                ProviderPayload = table.Column<string>(type: "jsonb", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inbox_messages", x => x.Id);
                table.ForeignKey("FK_inbox_messages_inbox_conversations_ConversationId", x => x.ConversationId, "inbox_conversations", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_inbox_conversations_OrganizationId_Status_LastMessageAt", "inbox_conversations", new[] { "OrganizationId", "Status", "LastMessageAt" });
        migrationBuilder.CreateIndex("IX_inbox_conversations_ChannelConnectionId_ExternalConversationId", "inbox_conversations", new[] { "ChannelConnectionId", "ExternalConversationId" }, unique: true);
        migrationBuilder.CreateIndex("IX_inbox_conversations_ContactId", "inbox_conversations", "ContactId");
        migrationBuilder.CreateIndex("IX_inbox_messages_ConversationId_CreatedAt", "inbox_messages", new[] { "ConversationId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_inbox_messages_ConversationId_ExternalMessageId", "inbox_messages", new[] { "ConversationId", "ExternalMessageId" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "inbox_messages");
        migrationBuilder.DropTable(name: "inbox_conversations");
    }
}
