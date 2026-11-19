using System;
using Majazi.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Majazi.Api.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261120110000_KnowledgeBase")]
public sealed class KnowledgeBase : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "knowledge_articles", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
            Title = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
            Slug = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
            Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
            Body = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: false),
            Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            Tags = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
            Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
            Version = table.Column<int>(type: "integer", nullable: false),
            CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
            UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
            PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_knowledge_articles", x => x.Id);
            table.ForeignKey("FK_knowledge_articles_organizations_OrganizationId", x => x.OrganizationId, "organizations", "Id", onDelete: ReferentialAction.Cascade);
        });

        migrationBuilder.CreateTable(name: "knowledge_article_revisions", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            ArticleId = table.Column<Guid>(type: "uuid", nullable: false),
            Version = table.Column<int>(type: "integer", nullable: false),
            Title = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
            Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
            Body = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: false),
            Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            Tags = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
            ChangeNote = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
            CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_knowledge_article_revisions", x => x.Id);
            table.ForeignKey("FK_knowledge_article_revisions_knowledge_articles_ArticleId", x => x.ArticleId, "knowledge_articles", "Id", onDelete: ReferentialAction.Cascade);
        });

        migrationBuilder.CreateIndex("IX_knowledge_articles_OrganizationId_Slug", "knowledge_articles", new[] { "OrganizationId", "Slug" }, unique: true);
        migrationBuilder.CreateIndex("IX_knowledge_articles_OrganizationId_Status_UpdatedAt", "knowledge_articles", new[] { "OrganizationId", "Status", "UpdatedAt" });
        migrationBuilder.CreateIndex("IX_knowledge_article_revisions_ArticleId_Version", "knowledge_article_revisions", new[] { "ArticleId", "Version" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "knowledge_article_revisions");
        migrationBuilder.DropTable(name: "knowledge_articles");
    }
}
