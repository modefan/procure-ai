using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureAi.Requests.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MoveToTenantSequences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_sequences",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_sequences", x => new { x.tenant_id, x.name });
                });

            // Existing request numbers keep counting from where they are. (request_number_counters was never in the Debezium
            // publication, so nothing leaves it: counters are not business data.)
            migrationBuilder.Sql(
                """
                INSERT INTO tenant_sequences (tenant_id, name, value)
                SELECT tenant_id, 'PR', last_value FROM request_number_counters
                """);

            migrationBuilder.DropTable(
                name: "request_number_counters");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "request_number_counters",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    last_value = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_number_counters", x => x.tenant_id);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO request_number_counters (tenant_id, last_value)
                SELECT tenant_id, value FROM tenant_sequences WHERE name = 'PR'
                """);

            migrationBuilder.DropTable(
                name: "tenant_sequences");
        }
    }
}
