using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRp.Migrations
{
    /// <inheritdoc />
    public partial class AddStoryPromptLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PromptLibraryJson",
                table: "ChatStories",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PromptLibraryJson",
                table: "ChatStories");
        }
    }
}
