using AgentRp.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRp.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppContext))]
    [Migration("20260423130000_AddStoryContextSettings")]
    public partial class AddStoryContextSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StoryContextJson",
                table: "ChatStories",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "{\"genre\":\"\",\"setting\":\"\",\"tone\":\"\",\"storyDirection\":\"\",\"explicitContent\":1,\"violentContent\":1}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StoryContextJson",
                table: "ChatStories");
        }
    }
}
