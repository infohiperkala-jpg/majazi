using Majazi.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Majazi.Api.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("202611180005_CampaignPerformance")]
public sealed class CampaignPerformance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(name: "ActualSpend", table: "marketing_campaigns", type: "numeric(18,2)", precision: 18, scale: 2, nullable: false, defaultValue: 0m);
        migrationBuilder.AddColumn<decimal>(name: "AttributedRevenue", table: "marketing_campaigns", type: "numeric(18,2)", precision: 18, scale: 2, nullable: false, defaultValue: 0m);
        migrationBuilder.AddColumn<long>(name: "Impressions", table: "marketing_campaigns", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<long>(name: "Clicks", table: "marketing_campaigns", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<long>(name: "Conversions", table: "marketing_campaigns", type: "bigint", nullable: false, defaultValue: 0L);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ActualSpend", table: "marketing_campaigns");
        migrationBuilder.DropColumn(name: "AttributedRevenue", table: "marketing_campaigns");
        migrationBuilder.DropColumn(name: "Impressions", table: "marketing_campaigns");
        migrationBuilder.DropColumn(name: "Clicks", table: "marketing_campaigns");
        migrationBuilder.DropColumn(name: "Conversions", table: "marketing_campaigns");
    }
}
