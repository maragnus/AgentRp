using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRp.Migrations
{
    /// <inheritdoc />
    public partial class AddThreadIdToStoryImageAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ThreadId",
                table: "StoryImageAssets",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.Sql("""
                UPDATE imageAsset
                SET ThreadId = imageLink.ThreadId
                FROM StoryImageAssets AS imageAsset
                CROSS APPLY (
                    SELECT TOP (1) link.ThreadId
                    FROM StoryImageLinks AS link
                    WHERE link.ImageId = imageAsset.Id
                    ORDER BY link.CreatedUtc DESC
                ) AS imageLink
                WHERE imageAsset.ThreadId = '00000000-0000-0000-0000-000000000000'
                """);

            migrationBuilder.CreateIndex(
                name: "IX_StoryImageAssets_ThreadId_CreatedUtc",
                table: "StoryImageAssets",
                columns: new[] { "ThreadId", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StoryImageAssets_ThreadId_CreatedUtc",
                table: "StoryImageAssets");

            migrationBuilder.DropColumn(
                name: "ThreadId",
                table: "StoryImageAssets");
        }
    }
}
