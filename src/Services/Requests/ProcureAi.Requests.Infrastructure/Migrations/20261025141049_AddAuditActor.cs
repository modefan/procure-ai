using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureAi.Requests.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditActor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "actor_id",
                table: "audit_entries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<string>(
                name: "calling_service",
                table: "audit_entries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "actor_id",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "calling_service",
                table: "audit_entries");
        }
    }
}
