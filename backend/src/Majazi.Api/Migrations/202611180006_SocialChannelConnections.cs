using Majazi.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Majazi.Api.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("202611180006_SocialChannelConnections")]
public partial class SocialChannelConnections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "social_channel_connections",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                BrandId = table.Column<Guid>(type: "uuid", nullable: true),
                Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                DisplayName = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                ExternalAccountId = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: true),
                EncryptedAccessToken = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_social_channel_connections", x => x.Id);
                table.ForeignKey("FK_social_channel_connections_brands_BrandId", x => x.BrandId, "brands", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_social_channel_connections_organizations_OrganizationId", x => x.OrganizationId, "organizations", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("IX_social_channel_connections_BrandId", "social_channel_connections", "BrandId");
        migrationBuilder.CreateIndex("IX_social_channel_connections_OrganizationId_Provider_ExternalAccountId", "social_channel_connections", new[] { "OrganizationId", "Provider", "ExternalAccountId" }, unique: true);
        migrationBuilder.CreateIndex("IX_social_channel_connections_OrganizationId_Status", "social_channel_connections", new[] { "OrganizationId", "Status" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "social_channel_connections");
}
