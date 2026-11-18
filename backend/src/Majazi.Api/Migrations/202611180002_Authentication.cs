using Majazi.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Majazi.Api.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("202611180002_Authentication")]
public sealed class Authentication : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "app_users",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                PasswordHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_app_users", x => x.Id));

        migrationBuilder.CreateTable(
            name: "organization_memberships",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_organization_memberships", x => x.Id);
                table.ForeignKey("FK_organization_memberships_app_users_UserId", x => x.UserId, "app_users", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_organization_memberships_organizations_OrganizationId", x => x.OrganizationId, "organizations", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_app_users_Email", "app_users", "Email", unique: true);
        migrationBuilder.CreateIndex("IX_organization_memberships_OrganizationId", "organization_memberships", "OrganizationId");
        migrationBuilder.CreateIndex("IX_organization_memberships_UserId_OrganizationId", "organization_memberships", new[] { "UserId", "OrganizationId" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "organization_memberships");
        migrationBuilder.DropTable(name: "app_users");
    }
}
