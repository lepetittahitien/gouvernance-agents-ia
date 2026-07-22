using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraceAgentApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPiiViolationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasPiiViolation",
                table: "agent_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PiiSummaryJson",
                table: "agent_runs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasPiiViolation",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "PiiSummaryJson",
                table: "agent_runs");
        }
    }
}
