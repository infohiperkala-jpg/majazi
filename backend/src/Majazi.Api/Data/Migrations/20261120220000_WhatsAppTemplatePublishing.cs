using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Majazi.Api.Data;

namespace Majazi.Api.Data.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261120220000_WhatsAppTemplatePublishing")]
public sealed class WhatsAppTemplatePublishing : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE content_drafts
                ADD COLUMN "WhatsAppTemplateName" varchar(512) NULL,
                ADD COLUMN "WhatsAppTemplateLanguage" varchar(35) NULL,
                ADD COLUMN "WhatsAppTemplateComponents" varchar(12000) NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE content_drafts
                DROP COLUMN IF EXISTS "WhatsAppTemplateName",
                DROP COLUMN IF EXISTS "WhatsAppTemplateLanguage",
                DROP COLUMN IF EXISTS "WhatsAppTemplateComponents";
            """);
    }
}
