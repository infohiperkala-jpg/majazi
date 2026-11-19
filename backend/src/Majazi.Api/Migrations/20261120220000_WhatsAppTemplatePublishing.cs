using System;
using Majazi.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Majazi.Api.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261120220000_WhatsAppTemplatePublishing")]
public sealed class WhatsAppTemplatePublishing : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "WhatsAppTemplateName",
            table: "content_drafts",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "WhatsAppTemplateLanguage",
            table: "content_drafts",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "WhatsAppTemplateParametersJson",
            table: "content_drafts",
            type: "character varying(4000)",
            maxLength: 4000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "WhatsAppTemplateName", table: "content_drafts");
        migrationBuilder.DropColumn(name: "WhatsAppTemplateLanguage", table: "content_drafts");
        migrationBuilder.DropColumn(name: "WhatsAppTemplateParametersJson", table: "content_drafts");
    }
}
