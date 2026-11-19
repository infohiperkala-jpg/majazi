using Majazi.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Majazi.Api.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261120230000_WhatsAppTemplateComponents")]
public sealed class WhatsAppTemplateComponents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "WhatsAppTemplateComponentsJson", table: "content_drafts",
            type: "character varying(12000)", maxLength: 12000, nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "WhatsAppTemplateComponentsJson", table: "content_drafts");
}
