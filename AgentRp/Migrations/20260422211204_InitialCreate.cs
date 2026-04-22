using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRp.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    JsonValue = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ChatThreads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsStarred = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActiveLeafMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SelectedSpeakerCharacterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SelectedAgentName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatThreads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MessageKind = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SpeakerCharacterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GenerationMode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceProcessRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ParentMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EditedFromMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatMessages_ChatThreads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "ChatThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatStories",
                columns: table => new
                {
                    ChatThreadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SceneJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CharactersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LocationsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ItemsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HistoryJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatStories", x => x.ChatThreadId);
                    table.ForeignKey(
                        name: "FK_ChatStories_ChatThreads_ChatThreadId",
                        column: x => x.ChatThreadId,
                        principalTable: "ChatThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProcessRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssistantMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorCharacterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ContextJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PlanningStartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PlanningCompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProseStartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProseCompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcessRuns_ChatThreads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "ChatThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StoryChatAppearanceEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SelectedLeafMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CoveredThroughMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CoveredThroughUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AppearanceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoryChatAppearanceEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoryChatAppearanceEntries_ChatThreads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "ChatThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StoryChatSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SelectedLeafMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CoveredThroughMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CoveredThroughUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IncludedMessageIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoryChatSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoryChatSnapshots_ChatThreads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "ChatThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProcessSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IconCssClass = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcessSteps_ProcessRuns_ProcessRunId",
                        column: x => x.ProcessRunId,
                        principalTable: "ProcessRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_EditedFromMessageId",
                table: "ChatMessages",
                column: "EditedFromMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ThreadId_CreatedUtc",
                table: "ChatMessages",
                columns: new[] { "ThreadId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ThreadId_ParentMessageId",
                table: "ChatMessages",
                columns: new[] { "ThreadId", "ParentMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ThreadId_SourceProcessRunId",
                table: "ChatMessages",
                columns: new[] { "ThreadId", "SourceProcessRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatThreads_IsStarred_UpdatedUtc",
                table: "ChatThreads",
                columns: new[] { "IsStarred", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatThreads_UpdatedUtc",
                table: "ChatThreads",
                column: "UpdatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessRuns_ThreadId_TargetMessageId",
                table: "ProcessRuns",
                columns: new[] { "ThreadId", "TargetMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessRuns_ThreadId_UserMessageId",
                table: "ProcessRuns",
                columns: new[] { "ThreadId", "UserMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessSteps_ProcessRunId_SortOrder",
                table: "ProcessSteps",
                columns: new[] { "ProcessRunId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_StoryChatAppearanceEntries_ThreadId_CoveredThroughMessageId",
                table: "StoryChatAppearanceEntries",
                columns: new[] { "ThreadId", "CoveredThroughMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_StoryChatAppearanceEntries_ThreadId_CreatedUtc",
                table: "StoryChatAppearanceEntries",
                columns: new[] { "ThreadId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StoryChatAppearanceEntries_ThreadId_SelectedLeafMessageId",
                table: "StoryChatAppearanceEntries",
                columns: new[] { "ThreadId", "SelectedLeafMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_StoryChatSnapshots_ThreadId_CoveredThroughMessageId",
                table: "StoryChatSnapshots",
                columns: new[] { "ThreadId", "CoveredThroughMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_StoryChatSnapshots_ThreadId_CreatedUtc",
                table: "StoryChatSnapshots",
                columns: new[] { "ThreadId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StoryChatSnapshots_ThreadId_SelectedLeafMessageId",
                table: "StoryChatSnapshots",
                columns: new[] { "ThreadId", "SelectedLeafMessageId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "ChatMessages");

            migrationBuilder.DropTable(
                name: "ChatStories");

            migrationBuilder.DropTable(
                name: "ProcessSteps");

            migrationBuilder.DropTable(
                name: "StoryChatAppearanceEntries");

            migrationBuilder.DropTable(
                name: "StoryChatSnapshots");

            migrationBuilder.DropTable(
                name: "ProcessRuns");

            migrationBuilder.DropTable(
                name: "ChatThreads");
        }
    }
}
