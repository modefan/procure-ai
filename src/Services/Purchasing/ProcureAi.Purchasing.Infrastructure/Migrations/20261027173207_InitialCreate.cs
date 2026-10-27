using System;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcureAi.BuildingBlocks.Persistence;

#nullable disable

namespace ProcureAi.Purchasing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        // The business tables and the interceptor rows. Not tenant_sequences (counters) nor processed_webhooks (replay protection).
        private static readonly string[] CapturedTables = ["purchase_orders", "order_lines", "goods_receipts", "receipt_lines", "audit_entries"];

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
                name: "processed_webhooks",
                columns: table => new
                {
                    source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_webhooks", x => new { x.source, x.event_id });
                });

            migrationBuilder.CreateTable(
                name: "purchase_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: true),
                    request_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    requester_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    cost_center_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    supplier_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    supplier_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    expected_delivery = table.Column<DateOnly>(type: "date", nullable: true),
                    supplier_attempts = table.Column<int>(type: "integer", nullable: false),
                    last_supplier_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    next_supplier_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    supplier_cancel_pending = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_orders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_sequences",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_sequences", x => new { x.tenant_id, x.name });
                });

            migrationBuilder.CreateTable(
                name: "goods_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivery_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_goods_receipts", x => x.id);
                    table.ForeignKey(
                        name: "FK_goods_receipts_purchase_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "purchase_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                columns: table => new
                {
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price_excl_vat = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    received = table.Column<int>(type: "integer", nullable: false),
                    closed_short = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_lines", x => new { x.order_id, x.line_no });
                    table.ForeignKey(
                        name: "FK_order_lines_purchase_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "purchase_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "receipt_lines",
                columns: table => new
                {
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipt_lines", x => new { x.receipt_id, x.line_no });
                    table.ForeignKey(
                        name: "FK_receipt_lines_goods_receipts_receipt_id",
                        column: x => x.receipt_id,
                        principalTable: "goods_receipts",
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
                name: "IX_goods_receipts_order_id",
                table: "goods_receipts",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_tenant_id_created_at",
                table: "purchase_orders",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_tenant_id_number",
                table: "purchase_orders",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_tenant_id_request_id",
                table: "purchase_orders",
                columns: new[] { "tenant_id", "request_id" },
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
                name: "order_lines");

            migrationBuilder.DropTable(
                name: "processed_webhooks");

            migrationBuilder.DropTable(
                name: "receipt_lines");

            migrationBuilder.DropTable(
                name: "tenant_sequences");

            migrationBuilder.DropTable(
                name: "goods_receipts");

            migrationBuilder.DropTable(
                name: "purchase_orders");
        }
    }
}
