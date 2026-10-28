using System;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcureAi.BuildingBlocks.Persistence;

#nullable disable

namespace ProcureAi.Budgets.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgetSpends : Migration
    {
        private static readonly string[] CapturedTables = ["budget_spends"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "spent",
                table: "budget_reservations",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "budget_spends",
                columns: table => new
                {
                    budget_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_spends", x => new { x.budget_id, x.payment_id });
                    table.ForeignKey(
                        name: "FK_budget_spends_budgets_budget_id",
                        column: x => x.budget_id,
                        principalTable: "budgets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.EnableCdc(CapturedTables);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableCdc(CapturedTables);

            migrationBuilder.DropTable(
                name: "budget_spends");

            migrationBuilder.DropColumn(
                name: "spent",
                table: "budget_reservations");
        }
    }
}
