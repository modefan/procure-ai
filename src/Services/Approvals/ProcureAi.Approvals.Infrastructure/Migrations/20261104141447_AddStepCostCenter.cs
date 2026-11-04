using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureAi.Approvals.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStepCostCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cost_center_code",
                table: "approval_steps",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            // Steps of a running saga get its cost center; steps whose saga is gone keep "" (the chain is then scoped by requester
            // and approvers only, see GetApprovalChain).
            migrationBuilder.Sql(
                "UPDATE approval_steps AS s SET cost_center_code = g.cost_center_code FROM approval_sagas AS g WHERE g.id = s.request_id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cost_center_code",
                table: "approval_steps");
        }
    }
}
