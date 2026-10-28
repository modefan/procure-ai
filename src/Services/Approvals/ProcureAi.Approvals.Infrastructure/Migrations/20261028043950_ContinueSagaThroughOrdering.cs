using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureAi.Approvals.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ContinueSagaThroughOrdering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "goods_received",
                table: "approval_sagas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "lines",
                table: "approval_sagas",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<Guid>(
                name: "order_id",
                table: "approval_sagas",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "order_number",
                table: "approval_sagas",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "state_before_cancel",
                table: "approval_sagas",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "supplier_id",
                table: "approval_sagas",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "goods_received",
                table: "approval_sagas");

            migrationBuilder.DropColumn(
                name: "lines",
                table: "approval_sagas");

            migrationBuilder.DropColumn(
                name: "order_id",
                table: "approval_sagas");

            migrationBuilder.DropColumn(
                name: "order_number",
                table: "approval_sagas");

            migrationBuilder.DropColumn(
                name: "state_before_cancel",
                table: "approval_sagas");

            migrationBuilder.DropColumn(
                name: "supplier_id",
                table: "approval_sagas");
        }
    }
}
