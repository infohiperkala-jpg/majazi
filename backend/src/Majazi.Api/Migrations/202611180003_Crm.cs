using Majazi.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Majazi.Api.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("202611180003_Crm")]
public sealed class Crm : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "crm_contacts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                BrandId = table.Column<Guid>(type: "uuid", nullable: true),
                FullName = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                Company = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: true),
                Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_crm_contacts", x => x.Id);
                table.ForeignKey("FK_crm_contacts_organizations_OrganizationId", x => x.OrganizationId, "organizations", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_crm_contacts_brands_BrandId", x => x.BrandId, "brands", "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "crm_leads",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                BrandId = table.Column<Guid>(type: "uuid", nullable: true),
                ContactId = table.Column<Guid>(type: "uuid", nullable: true),
                Title = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                EstimatedValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                Source = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_crm_leads", x => x.Id);
                table.ForeignKey("FK_crm_leads_organizations_OrganizationId", x => x.OrganizationId, "organizations", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_crm_leads_brands_BrandId", x => x.BrandId, "brands", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_crm_leads_crm_contacts_ContactId", x => x.ContactId, "crm_contacts", "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "crm_activities",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                ContactId = table.Column<Guid>(type: "uuid", nullable: true),
                LeadId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                ActivityType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_crm_activities", x => x.Id);
                table.ForeignKey("FK_crm_activities_organizations_OrganizationId", x => x.OrganizationId, "organizations", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_crm_activities_crm_contacts_ContactId", x => x.ContactId, "crm_contacts", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_crm_activities_crm_leads_LeadId", x => x.LeadId, "crm_leads", "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex("IX_crm_contacts_OrganizationId_CreatedAt", "crm_contacts", new[] { "OrganizationId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_crm_contacts_BrandId", "crm_contacts", "BrandId");
        migrationBuilder.CreateIndex("IX_crm_leads_OrganizationId_Stage_CreatedAt", "crm_leads", new[] { "OrganizationId", "Stage", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_crm_leads_BrandId", "crm_leads", "BrandId");
        migrationBuilder.CreateIndex("IX_crm_leads_ContactId", "crm_leads", "ContactId");
        migrationBuilder.CreateIndex("IX_crm_activities_OrganizationId_OccurredAt", "crm_activities", new[] { "OrganizationId", "OccurredAt" });
        migrationBuilder.CreateIndex("IX_crm_activities_ContactId", "crm_activities", "ContactId");
        migrationBuilder.CreateIndex("IX_crm_activities_LeadId", "crm_activities", "LeadId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("crm_activities");
        migrationBuilder.DropTable("crm_leads");
        migrationBuilder.DropTable("crm_contacts");
    }
}
