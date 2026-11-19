using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Majazi.Api.Data;

namespace Majazi.Api.Data.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261119110000_ContentPublishingQueue")]
public sealed class ContentPublishingQueue : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE content_drafts
                ADD COLUMN "ScheduledAt" timestamptz NULL,
                ADD COLUMN "PublishedAt" timestamptz NULL,
                ADD COLUMN "PublishConnectionId" uuid NULL,
                ADD COLUMN "PublishTarget" varchar(180) NULL,
                ADD COLUMN "PublishError" varchar(1000) NULL;
            CREATE INDEX ix_content_drafts_status_scheduled_at
                ON content_drafts ("Status", "ScheduledAt");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS ix_content_drafts_status_scheduled_at;
            ALTER TABLE content_drafts
                DROP COLUMN IF EXISTS "ScheduledAt",
                DROP COLUMN IF EXISTS "PublishedAt",
                DROP COLUMN IF EXISTS "PublishConnectionId",
                DROP COLUMN IF EXISTS "PublishTarget",
                DROP COLUMN IF EXISTS "PublishError";
            """);
    }
}
