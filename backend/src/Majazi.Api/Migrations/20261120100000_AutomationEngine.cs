using System;
using Majazi.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Majazi.Api.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261120100000_AutomationEngine")]
public sealed class AutomationEngine : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "automation_rules",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Trigger = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ConditionField = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ConditionOperator = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                ConditionValue = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                ActionType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ActivityType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                ActionValue = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_automation_rules", x => x.Id);
                table.ForeignKey("FK_automation_rules_organizations_OrganizationId", x => x.OrganizationId, "organizations", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "automation_runs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                Trigger = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                SubjectType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                Result = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_automation_runs", x => x.Id);
                table.ForeignKey("FK_automation_runs_automation_rules_RuleId", x => x.RuleId, "automation_rules", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_automation_runs_organizations_OrganizationId", x => x.OrganizationId, "organizations", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_automation_rules_OrganizationId_Trigger_IsEnabled", "automation_rules", new[] { "OrganizationId", "Trigger", "IsEnabled" });
        migrationBuilder.CreateIndex("IX_automation_runs_Status_CreatedAt", "automation_runs", new[] { "Status", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_automation_runs_OrganizationId_RuleId_CreatedAt", "automation_runs", new[] { "OrganizationId", "RuleId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_automation_runs_RuleId", "automation_runs", "RuleId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "automation_runs");
        migrationBuilder.DropTable(name: "automation_rules");
    }
}
