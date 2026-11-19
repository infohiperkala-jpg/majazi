using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Majazi.Api.Data;

namespace Majazi.Api.Data.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261120210000_EitaaContentAttachments")]
public sealed class EitaaContentAttachments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE content_draft_attachments (
                "Id" uuid NOT NULL PRIMARY KEY,
                "DraftId" uuid NOT NULL REFERENCES content_drafts ("Id") ON DELETE CASCADE,
                "FileName" varchar(255) NOT NULL,
                "ContentType" varchar(160) NOT NULL,
                "Size" bigint NOT NULL,
                "Data" bytea NOT NULL,
                "CreatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX ix_content_draft_attachments_draft_id
                ON content_draft_attachments ("DraftId");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS content_draft_attachments;");
    }
}
