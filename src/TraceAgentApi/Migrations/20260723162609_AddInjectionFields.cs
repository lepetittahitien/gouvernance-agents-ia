using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraceAgentApi.Migrations
{
    /// <inheritdoc />
    public partial class AddInjectionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InjectionRisk",
                table: "agent_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "InjectionSummaryJson",
                table: "agent_runs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InjectionRisk",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "InjectionSummaryJson",
                table: "agent_runs");
        }
    }
}
