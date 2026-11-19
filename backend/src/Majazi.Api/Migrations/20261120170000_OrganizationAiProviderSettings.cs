using System;
using Majazi.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Majazi.Api.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261120170000_OrganizationAiProviderSettings")]
public sealed class OrganizationAiProviderSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "organization_ai_provider_settings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                BaseUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Model = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                EncryptedApiKey = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: true),
                IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_organization_ai_provider_settings", x => x.Id);
                table.ForeignKey("FK_organization_ai_provider_settings_organizations_OrganizationId", x => x.OrganizationId, "organizations", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("IX_organization_ai_provider_settings_OrganizationId", "organization_ai_provider_settings", "OrganizationId", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "organization_ai_provider_settings");
    }
}
