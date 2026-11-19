using System;
using Majazi.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Majazi.Api.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261120200000_AutomationRunIdempotency")]
public sealed class AutomationRunIdempotency : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "EventKey",
            table: "automation_runs",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_automation_runs_OrganizationId_RuleId_EventKey",
            table: "automation_runs",
            columns: new[] { "OrganizationId", "RuleId", "EventKey" },
            unique: true,
            filter: "\"EventKey\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_automation_runs_OrganizationId_RuleId_EventKey",
            table: "automation_runs");

        migrationBuilder.DropColumn(name: "EventKey", table: "automation_runs");
    }
}
