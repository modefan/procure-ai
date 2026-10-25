using Microsoft.EntityFrameworkCore.Migrations;
using ProcureAi.BuildingBlocks.Persistence;

#nullable disable

namespace ProcureAi.Requests.Infrastructure.Migrations
{
    /// <summary>
    /// Brings databases created before audit_entries was captured up to date: the publication gets audit_entries
    /// (idempotent for the tables that are already in it), and the replication slot of the old single Debezium
    /// (debezium_requests, replaced by debezium_requestsdb) is dropped when nothing uses it, because an unused logical
    /// slot stops Postgres from recycling WAL.
    /// </summary>
    public partial class CaptureAuditEntries : Migration
    {
        private const string LegacySlot = "debezium_requests";

        private static readonly string[] CapturedTables = ["purchase_requests", "request_lines", "audit_entries"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnableCdc(CapturedTables);
            migrationBuilder.DropInactiveReplicationSlot(LegacySlot);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the earlier migration owns the capture of the tables, and a dropped slot is recreated by Debezium.
        }
    }
}
