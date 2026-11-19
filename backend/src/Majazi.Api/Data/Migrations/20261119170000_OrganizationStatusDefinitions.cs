using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Majazi.Api.Data;

namespace Majazi.Api.Data.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261119170000_OrganizationStatusDefinitions")]
public sealed class OrganizationStatusDefinitions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE status_definitions (
                "Id" uuid NOT NULL PRIMARY KEY,
                "OrganizationId" uuid NOT NULL REFERENCES organizations ("Id") ON DELETE CASCADE,
                "Module" varchar(80) NOT NULL,
                "Key" varchar(64) NOT NULL,
                "Label" varchar(100) NOT NULL,
                "Color" varchar(7) NOT NULL DEFAULT '#64748b',
                "Description" varchar(500) NULL,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX ix_status_definitions_org_module_key
                ON status_definitions ("OrganizationId", "Module", "Key");
            CREATE INDEX ix_status_definitions_org_module_order
                ON status_definitions ("OrganizationId", "Module", "SortOrder");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS status_definitions;");
    }
}
