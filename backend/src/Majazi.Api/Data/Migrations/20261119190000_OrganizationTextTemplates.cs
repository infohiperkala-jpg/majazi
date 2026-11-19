using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Majazi.Api.Data;

namespace Majazi.Api.Data.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261119190000_OrganizationTextTemplates")]
public sealed class OrganizationTextTemplates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE text_templates (
                "Id" uuid NOT NULL PRIMARY KEY,
                "OrganizationId" uuid NOT NULL REFERENCES organizations ("Id") ON DELETE CASCADE,
                "Module" varchar(80) NOT NULL,
                "Field" varchar(80) NOT NULL,
                "Name" varchar(120) NOT NULL,
                "Content" varchar(8000) NOT NULL,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX ix_text_templates_org_module_field_name
                ON text_templates ("OrganizationId", "Module", "Field", "Name");
            CREATE INDEX ix_text_templates_org_module_field_order
                ON text_templates ("OrganizationId", "Module", "Field", "SortOrder");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS text_templates;");
    }
}
