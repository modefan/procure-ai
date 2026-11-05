using Microsoft.EntityFrameworkCore.Migrations;
using ProcureAi.BuildingBlocks.Persistence;

#nullable disable

namespace ProcureAi.Invoicing.Infrastructure.Migrations
{
    /// <summary>
    /// The table Debezium's heartbeat updates (debezium_heartbeat, one row), in the publication: an idle database still moves its
    /// replication slot, so the slot does not hold back the server's WAL (plan 08 Task 1). Not part of the EF model.
    /// </summary>
    public partial class AddDebeziumHeartbeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnableHeartbeat();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableHeartbeat();
        }
    }
}
