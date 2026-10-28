using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureAi.Approvals.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStepOpenedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "opened_at",
                table: "approval_steps",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "opened_at",
                table: "approval_steps");
        }
    }
}
