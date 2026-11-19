using System;
using Majazi.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Majazi.Api.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261120150000_OrganizationAuditTrail")]
public sealed class OrganizationAuditTrail : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "organization_audit_events",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                MetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_organization_audit_events", x => x.Id);
                table.ForeignKey("FK_organization_audit_events_organizations_OrganizationId", x => x.OrganizationId, "organizations", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("IX_organization_audit_events_OrganizationId_CreatedAt", "organization_audit_events", new[] { "OrganizationId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_organization_audit_events_OrganizationId_Action_CreatedAt", "organization_audit_events", new[] { "OrganizationId", "Action", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "organization_audit_events");
    }
}
