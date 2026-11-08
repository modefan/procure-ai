using Microsoft.EntityFrameworkCore.Migrations;
using ProcureAi.BuildingBlocks.Persistence;

#nullable disable

namespace ProcureAi.Access.Infrastructure.Migrations
{
    /// <summary>
    /// Captures <c>system_settings</c> (the demo switch) with Debezium like the other Access tables, so a switch made outside the app (manual
    /// SQL) is flagged in the data change audit too. The AppHost's and the chart's Access table lists name it as well.
    /// </summary>
    public partial class CaptureSystemSettings : Migration
    {
        private static readonly string[] CapturedTables = ["system_settings"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnableCdc(CapturedTables);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableCdc(CapturedTables);
        }
    }
}
