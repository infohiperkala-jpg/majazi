using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Majazi.Api.Data;

namespace Majazi.Api.Data.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261119130000_ContentApprovalWorkflow")]
public sealed class ContentApprovalWorkflow : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE content_drafts
                ADD COLUMN "ApprovalRound" integer NOT NULL DEFAULT 1;

            CREATE TABLE content_approval_records (
                "Id" uuid NOT NULL PRIMARY KEY,
                "DraftId" uuid NOT NULL REFERENCES content_drafts ("Id") ON DELETE CASCADE,
                "OrganizationId" uuid NOT NULL REFERENCES organizations ("Id") ON DELETE CASCADE,
                "ReviewRound" integer NOT NULL DEFAULT 1,
                "StageNumber" integer NOT NULL,
                "Decision" varchar(20) NOT NULL,
                "Comment" varchar(1000) NULL,
                "ActorUserId" uuid NOT NULL,
                "CreatedAt" timestamptz NOT NULL
            );

            CREATE UNIQUE INDEX ix_content_approval_records_draft_round_stage
                ON content_approval_records ("DraftId", "ReviewRound", "StageNumber");
            CREATE INDEX ix_content_approval_records_org_created
                ON content_approval_records ("OrganizationId", "CreatedAt");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS content_approval_records;
            ALTER TABLE content_drafts DROP COLUMN IF EXISTS "ApprovalRound";
            """);
    }
}
