using System;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcureAi.BuildingBlocks.Persistence;

#nullable disable

namespace ProcureAi.Approvals.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        // The saga table (approval_sagas) is workflow state, not captured; the steps carry the business story.
        private static readonly string[] CapturedTables = ["approval_steps", "audit_entries"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "approval_sagas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    request_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requester_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    cost_center_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    total_incl_vat = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    current_step_no = table.Column<int>(type: "integer", nullable: false),
                    cancel_requested = table.Column<bool>(type: "boolean", nullable: false),
                    overdue_alert_step_id = table.Column<Guid>(type: "uuid", nullable: true),
                    overdue_reminders_sent = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_sagas", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "approval_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    request_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requester_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    total_incl_vat = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    step_no = table.Column<int>(type: "integer", nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    acted_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    acted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reminders_sent = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    approver_ids = table.Column<string[]>(type: "text[]", nullable: false),
                    covers_levels = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_steps", x => x.id);
                });

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

            migrationBuilder.CreateIndex(
                name: "IX_approval_steps_tenant_id_request_id_step_no",
                table: "approval_steps",
                columns: new[] { "tenant_id", "request_id", "step_no" });

            migrationBuilder.CreateIndex(
                name: "IX_approval_steps_tenant_id_status",
                table: "approval_steps",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_table_name_primary_key",
                table: "audit_entries",
                columns: new[] { "table_name", "primary_key" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_transaction_id",
                table: "audit_entries",
                column: "transaction_id");

            migrationBuilder.EnableCdc(CapturedTables);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableCdc(CapturedTables);

            migrationBuilder.DropTable(
                name: "approval_sagas");

            migrationBuilder.DropTable(
                name: "approval_steps");

            migrationBuilder.DropTable(
                name: "audit_entries");
        }
    }
}
