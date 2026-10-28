using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureAi.Requests.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FollowOrderingAndCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cancellation_refused_reason",
                table: "purchase_requests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at",
                table: "purchase_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "order_id",
                table: "purchase_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "order_number",
                table: "purchase_requests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "spent_incl_vat",
                table: "purchase_requests",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status_before_cancel",
                table: "purchase_requests",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cancellation_refused_reason",
                table: "purchase_requests");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "purchase_requests");

            migrationBuilder.DropColumn(
                name: "order_id",
                table: "purchase_requests");

            migrationBuilder.DropColumn(
                name: "order_number",
                table: "purchase_requests");

            migrationBuilder.DropColumn(
                name: "spent_incl_vat",
                table: "purchase_requests");

            migrationBuilder.DropColumn(
                name: "status_before_cancel",
                table: "purchase_requests");
        }
    }
}
