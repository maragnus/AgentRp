using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRp.Migrations
{
    /// <inheritdoc />
    public partial class AddAiProvidersAndModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SelectedAiModelId",
                table: "ChatThreads",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProviderKind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    BaseEndpoint = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ApiKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ManagementApiKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ProjectId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TeamId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastDiscoveredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastDiscoveryError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastMetricsRefreshUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastMetricsError = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderModelId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Repository = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    UseJsonSchemaResponseFormat = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    PlanningSettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WritingSettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiModels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiModels_AiProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "AiProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiProviderMetrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MetricKind = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RefreshedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProviderMetrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiProviderMetrics_AiProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "AiProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatThreads_SelectedAiModelId",
                table: "ChatThreads",
                column: "SelectedAiModelId");

            migrationBuilder.CreateIndex(
                name: "IX_AiModels_IsEnabled_SortOrder",
                table: "AiModels",
                columns: new[] { "IsEnabled", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_AiModels_ProviderId_ProviderModelId",
                table: "AiModels",
                columns: new[] { "ProviderId", "ProviderModelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiProviderMetrics_ProviderId_MetricKind",
                table: "AiProviderMetrics",
                columns: new[] { "ProviderId", "MetricKind" });

            migrationBuilder.CreateIndex(
                name: "IX_AiProviders_IsEnabled_SortOrder",
                table: "AiProviders",
                columns: new[] { "IsEnabled", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_AiProviders_Name",
                table: "AiProviders",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ChatThreads_AiModels_SelectedAiModelId",
                table: "ChatThreads",
                column: "SelectedAiModelId",
                principalTable: "AiModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatThreads_AiModels_SelectedAiModelId",
                table: "ChatThreads");

            migrationBuilder.DropTable(
                name: "AiModels");

            migrationBuilder.DropTable(
                name: "AiProviderMetrics");

            migrationBuilder.DropTable(
                name: "AiProviders");

            migrationBuilder.DropIndex(
                name: "IX_ChatThreads_SelectedAiModelId",
                table: "ChatThreads");

            migrationBuilder.DropColumn(
                name: "SelectedAiModelId",
                table: "ChatThreads");
        }
    }
}
