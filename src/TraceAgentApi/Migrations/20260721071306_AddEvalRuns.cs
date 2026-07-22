using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TraceAgentApi.Migrations
{
    /// <inheritdoc />
    public partial class AddEvalRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "eval_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModelName = table.Column<string>(type: "text", nullable: false),
                    CasesTotal = table.Column<int>(type: "integer", nullable: false),
                    CasesPassed = table.Column<int>(type: "integer", nullable: false),
                    ScorePercent = table.Column<double>(type: "double precision", nullable: false),
                    TotalDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    TotalTokens = table.Column<int>(type: "integer", nullable: false),
                    CaseResultsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eval_runs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "eval_runs");
        }
    }
}
