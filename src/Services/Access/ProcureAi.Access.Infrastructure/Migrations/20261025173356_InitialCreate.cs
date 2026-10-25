using System;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcureAi.BuildingBlocks.Persistence;

#nullable disable

namespace ProcureAi.Access.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        private static readonly string[] CapturedTables =
            ["permission_matrices", "permission_grants", "departments", "cost_centers", "tenant_users", "audit_entries"];

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
                name: "cost_centers",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    department_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    manager_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cost_centers", x => new { x.tenant_id, x.code });
                });

            migrationBuilder.CreateTable(
                name: "departments",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    head_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_departments", x => new { x.tenant_id, x.code });
                });

            migrationBuilder.CreateTable(
                name: "permission_matrices",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permission_matrices", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_users",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    roles = table.Column<string[]>(type: "text[]", nullable: false),
                    approval_limit = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    is_finance_approver = table.Column<bool>(type: "boolean", nullable: false),
                    cost_center_codes = table.Column<string[]>(type: "text[]", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_users", x => new { x.tenant_id, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "permission_grants",
                columns: table => new
                {
                    role = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    permission = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permission_grants", x => new { x.tenant_id, x.role, x.permission });
                    table.ForeignKey(
                        name: "FK_permission_grants_permission_matrices_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "permission_matrices",
                        principalColumn: "tenant_id",
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

            // Change data capture: Debezium Server reads this database through the publication dbz_publication (see docs/service-template.md).
            migrationBuilder.EnableCdc(CapturedTables);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableCdc(CapturedTables);

            migrationBuilder.DropTable(
                name: "audit_entries");

            migrationBuilder.DropTable(
                name: "cost_centers");

            migrationBuilder.DropTable(
                name: "departments");

            migrationBuilder.DropTable(
                name: "permission_grants");

            migrationBuilder.DropTable(
                name: "tenant_users");

            migrationBuilder.DropTable(
                name: "permission_matrices");
        }
    }
}
