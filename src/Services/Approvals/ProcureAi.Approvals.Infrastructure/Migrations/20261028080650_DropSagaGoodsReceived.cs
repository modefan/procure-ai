using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureAi.Approvals.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropSagaGoodsReceived : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "goods_received",
                table: "approval_sagas");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "goods_received",
                table: "approval_sagas",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
