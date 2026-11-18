using Majazi.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Majazi.Api.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("202611180004_MarketingCampaigns")]
public sealed class MarketingCampaigns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "marketing_campaigns",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                BrandId = table.Column<Guid>(type: "uuid", nullable: true),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Objective = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Channel = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Budget = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_marketing_campaigns", x => x.Id);
                table.ForeignKey("FK_marketing_campaigns_organizations_OrganizationId", x => x.OrganizationId, "organizations", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_marketing_campaigns_brands_BrandId", x => x.BrandId, "brands", "Id", onDelete: ReferentialAction.SetNull);
            });
        migrationBuilder.CreateIndex("IX_marketing_campaigns_OrganizationId_Status_StartsAt", "marketing_campaigns", new[] { "OrganizationId", "Status", "StartsAt" });
        migrationBuilder.CreateIndex("IX_marketing_campaigns_BrandId", "marketing_campaigns", "BrandId");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("marketing_campaigns");
}
