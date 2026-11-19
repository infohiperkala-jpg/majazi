using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Majazi.Api.Data;

namespace Majazi.Api.Data.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261119090000_ContentStudioFoundation")]
public sealed class ContentStudioFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE content_drafts (
                "Id" uuid PRIMARY KEY,
                "OrganizationId" uuid NOT NULL REFERENCES organizations("Id") ON DELETE CASCADE,
                "BrandId" uuid NULL REFERENCES brands("Id") ON DELETE SET NULL,
                "CreatedByUserId" uuid NOT NULL,
                "Title" varchar(240) NOT NULL,
                "Content" varchar(30000) NOT NULL,
                "Channel" varchar(40) NOT NULL,
                "Tone" varchar(40) NOT NULL,
                "Status" varchar(24) NOT NULL,
                "Prompt" varchar(4000) NULL,
                "AiModel" varchar(120) NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                "ApprovedAt" timestamptz NULL,
                "ApprovedByUserId" uuid NULL
            );
            CREATE INDEX ix_content_drafts_org_status_updated
                ON content_drafts ("OrganizationId", "Status", "UpdatedAt" DESC);
            CREATE TABLE content_draft_versions (
                "Id" uuid PRIMARY KEY,
                "DraftId" uuid NOT NULL REFERENCES content_drafts("Id") ON DELETE CASCADE,
                "VersionNumber" integer NOT NULL,
                "Title" varchar(240) NOT NULL,
                "Content" varchar(30000) NOT NULL,
                "ChangeNote" varchar(300) NOT NULL,
                "CreatedByUserId" uuid NOT NULL,
                "CreatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX ix_content_draft_versions_draft_version
                ON content_draft_versions ("DraftId", "VersionNumber");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS content_draft_versions;
            DROP TABLE IF EXISTS content_drafts;
            """);
    }
}
