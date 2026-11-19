using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Majazi.Api.Data;

namespace Majazi.Api.Data.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261119150000_AutonomousGrowthAgent")]
public sealed class AutonomousGrowthAgent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE organizations
                ADD COLUMN "GrowthAgentEnabled" boolean NOT NULL DEFAULT false,
                ADD COLUMN "GrowthAgentLastRunAt" timestamptz NULL;
            CREATE INDEX ix_organizations_growth_agent_due
                ON organizations ("GrowthAgentEnabled", "GrowthAgentLastRunAt");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS ix_organizations_growth_agent_due;
            ALTER TABLE organizations
                DROP COLUMN IF EXISTS "GrowthAgentEnabled",
                DROP COLUMN IF EXISTS "GrowthAgentLastRunAt";
            """);
    }
}
