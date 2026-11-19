using System;
using Majazi.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Majazi.Api.Migrations;

[DbContext(typeof(MajaziDbContext))]
[Migration("20261120190000_SalesOrdersAndPayments")]
public sealed class SalesOrdersAndPayments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "sales_orders", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
            ContactId = table.Column<Guid>(type: "uuid", nullable: true),
            OrderNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
            Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
            Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
            Subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
            Discount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
            Tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
            Total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
            Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
            Version = table.Column<int>(type: "integer", nullable: false),
            CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_sales_orders", x => x.Id);
            table.ForeignKey("FK_sales_orders_organizations_OrganizationId", x => x.OrganizationId, "organizations", "Id", onDelete: ReferentialAction.Cascade);
            table.ForeignKey("FK_sales_orders_crm_contacts_ContactId", x => x.ContactId, "crm_contacts", "Id", onDelete: ReferentialAction.SetNull);
        });
        migrationBuilder.CreateTable(name: "sales_order_lines", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            OrderId = table.Column<Guid>(type: "uuid", nullable: false),
            Name = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
            Sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
            Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
            UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
            Discount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
            LineTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
            SortOrder = table.Column<int>(type: "integer", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_sales_order_lines", x => x.Id);
            table.ForeignKey("FK_sales_order_lines_sales_orders_OrderId", x => x.OrderId, "sales_orders", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateTable(name: "payment_transactions", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            OrderId = table.Column<Guid>(type: "uuid", nullable: false),
            Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
            Method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
            Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
            Reference = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: true),
            Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
            PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_payment_transactions", x => x.Id);
            table.ForeignKey("FK_payment_transactions_sales_orders_OrderId", x => x.OrderId, "sales_orders", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_sales_orders_OrganizationId_OrderNumber", "sales_orders", new[] { "OrganizationId", "OrderNumber" }, unique: true);
        migrationBuilder.CreateIndex("IX_sales_orders_OrganizationId_Status_CreatedAt", "sales_orders", new[] { "OrganizationId", "Status", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_sales_orders_ContactId", "sales_orders", "ContactId");
        migrationBuilder.CreateIndex("IX_sales_order_lines_OrderId_SortOrder", "sales_order_lines", new[] { "OrderId", "SortOrder" });
        migrationBuilder.CreateIndex("IX_payment_transactions_OrderId_CreatedAt", "payment_transactions", new[] { "OrderId", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "payment_transactions");
        migrationBuilder.DropTable(name: "sales_order_lines");
        migrationBuilder.DropTable(name: "sales_orders");
    }
}
