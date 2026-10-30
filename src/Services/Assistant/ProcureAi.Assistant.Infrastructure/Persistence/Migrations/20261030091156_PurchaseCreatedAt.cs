using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureAi.Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseCreatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "purchases",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()"); // rows from before get the full grace period
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "created_at",
                table: "purchases");
        }
    }
}
