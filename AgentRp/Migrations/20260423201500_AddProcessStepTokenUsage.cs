using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRp.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessStepTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InputTokenCount",
                table: "ProcessSteps",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OutputTokenCount",
                table: "ProcessSteps",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotalTokenCount",
                table: "ProcessSteps",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InputTokenCount",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "OutputTokenCount",
                table: "ProcessSteps");

            migrationBuilder.DropColumn(
                name: "TotalTokenCount",
                table: "ProcessSteps");
        }
    }
}
