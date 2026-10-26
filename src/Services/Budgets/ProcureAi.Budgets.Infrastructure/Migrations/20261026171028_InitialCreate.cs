using System;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcureAi.BuildingBlocks.Persistence;

#nullable disable

namespace ProcureAi.Budgets.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        private static readonly string[] CapturedTables = ["budgets", "budget_reservations", "audit_entries"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    calling_service = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    transaction_id = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "pg_current_xact_id()::xid::text::bigint"),
                    table_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    primary_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    operation = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    changes = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "budgets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    cost_center_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    fiscal_year = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    spent = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    reserved = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budgets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "request_markers",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_released = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_markers", x => new { x.tenant_id, x.request_id });
                });

            migrationBuilder.CreateTable(
                name: "budget_reservations",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    budget_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    is_released = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_reservations", x => new { x.budget_id, x.request_id });
                    table.ForeignKey(
                        name: "FK_budget_reservations_budgets_budget_id",
                        column: x => x.budget_id,
                        principalTable: "budgets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_table_name_primary_key",
                table: "audit_entries",
                columns: new[] { "table_name", "primary_key" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_transaction_id",
                table: "audit_entries",
                column: "transaction_id");

            migrationBuilder.CreateIndex(
                name: "IX_budgets_tenant_id_cost_center_code_fiscal_year",
                table: "budgets",
                columns: new[] { "tenant_id", "cost_center_code", "fiscal_year" },
                unique: true);

            migrationBuilder.EnableCdc(CapturedTables);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableCdc(CapturedTables);

            migrationBuilder.DropTable(
                name: "audit_entries");

            migrationBuilder.DropTable(
                name: "budget_reservations");

            migrationBuilder.DropTable(
                name: "request_markers");

            migrationBuilder.DropTable(
                name: "budgets");
        }
    }
}
