using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureAi.Requests.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEntriesAndCdc : Migration
    {
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
                name: "IX_audit_entries_table_name_primary_key",
                table: "audit_entries",
                columns: new[] { "table_name", "primary_key" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_transaction_id",
                table: "audit_entries",
                column: "transaction_id");

            // Change data capture: Debezium reads the Postgres WAL through the publication dbz_publication (created
            // empty by infra/postgres/initdb/01-debezium.sh, owned by the superuser). The business tables join it here.
            // audit_entries and the wolverine schema are deliberately NOT added: they are internal, not business data.
            // Needs the publication's owner (ALTER PUBLICATION) who also owns the tables: the role that migrates is the
            // database superuser in the AppHost and in the tests. A plain database without the publication skips this.
            // REPLICA IDENTITY FULL puts the complete old row in every update/delete event, so a change made outside the
            // app shows what it overwrote.
            migrationBuilder.Sql(CaptureSql("ADD"));
            migrationBuilder.Sql("ALTER TABLE public.purchase_requests REPLICA IDENTITY FULL;");
            migrationBuilder.Sql("ALTER TABLE public.request_lines REPLICA IDENTITY FULL;");
        }

        // Idempotent: only touches tables that are (not) yet in the publication.
        private static string CaptureSql(string action) => $$"""
            DO $$
            DECLARE t text;
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_publication WHERE pubname = 'dbz_publication') THEN
                    FOREACH t IN ARRAY ARRAY['purchase_requests', 'request_lines'] LOOP
                        IF {{(action == "ADD" ? "NOT " : "")}}EXISTS (SELECT 1 FROM pg_publication_tables
                                    WHERE pubname = 'dbz_publication' AND schemaname = 'public' AND tablename = t) THEN
                            EXECUTE format('ALTER PUBLICATION dbz_publication {{action}} TABLE public.%I', t);
                        END IF;
                    END LOOP;
                END IF;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CaptureSql("DROP"));
            migrationBuilder.Sql("ALTER TABLE public.purchase_requests REPLICA IDENTITY DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE public.request_lines REPLICA IDENTITY DEFAULT;");
            migrationBuilder.DropTable(
                name: "audit_entries");
        }
    }
}
