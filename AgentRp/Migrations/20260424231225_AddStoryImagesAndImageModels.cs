using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRp.Migrations
{
    /// <inheritdoc />
    public partial class AddStoryImagesAndImageModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsImageModelEnabled",
                table: "AiModels",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTextModelEnabled",
                table: "AiModels",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "StoryImageAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Bytes = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Width = table.Column<int>(type: "int", nullable: true),
                    Height = table.Column<int>(type: "int", nullable: true),
                    SourceKind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    UserPrompt = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FinalPrompt = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GenerationRationale = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AiProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AiProviderKind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    AiProviderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AiModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AiModelName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ProviderModelId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoryImageAssets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoryImageLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ImageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityKind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProcessRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Purpose = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoryImageLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoryImageLinks_ChatThreads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "ChatThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StoryImageLinks_StoryImageAssets_ImageId",
                        column: x => x.ImageId,
                        principalTable: "StoryImageAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoryImageAssets_CreatedUtc",
                table: "StoryImageAssets",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_StoryImageLinks_ImageId",
                table: "StoryImageLinks",
                column: "ImageId");

            migrationBuilder.CreateIndex(
                name: "IX_StoryImageLinks_MessageId",
                table: "StoryImageLinks",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_StoryImageLinks_ProcessRunId",
                table: "StoryImageLinks",
                column: "ProcessRunId");

            migrationBuilder.CreateIndex(
                name: "IX_StoryImageLinks_ThreadId_EntityKind_EntityId",
                table: "StoryImageLinks",
                columns: new[] { "ThreadId", "EntityKind", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_StoryImageLinks_ThreadId_ImageId",
                table: "StoryImageLinks",
                columns: new[] { "ThreadId", "ImageId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoryImageLinks");

            migrationBuilder.DropTable(
                name: "StoryImageAssets");

            migrationBuilder.DropColumn(
                name: "IsImageModelEnabled",
                table: "AiModels");

            migrationBuilder.DropColumn(
                name: "IsTextModelEnabled",
                table: "AiModels");
        }
    }
}
