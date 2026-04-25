using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRp.Migrations
{
    [Migration("20260424181357_AddProcessRunAiAttribution")]
    public partial class AddProcessRunAiAttribution : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AiModelId",
                table: "ProcessRuns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AiProviderId",
                table: "ProcessRuns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiProviderKind",
                table: "ProcessRuns",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessRuns_ThreadId_AiProviderId",
                table: "ProcessRuns",
                columns: new[] { "ThreadId", "AiProviderId" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessRuns_ThreadId_AiProviderId",
                table: "ProcessRuns");

            migrationBuilder.DropColumn(
                name: "AiModelId",
                table: "ProcessRuns");

            migrationBuilder.DropColumn(
                name: "AiProviderId",
                table: "ProcessRuns");

            migrationBuilder.DropColumn(
                name: "AiProviderKind",
                table: "ProcessRuns");
        }
    }
}
