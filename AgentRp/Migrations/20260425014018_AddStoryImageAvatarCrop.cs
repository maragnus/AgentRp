using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRp.Migrations
{
    /// <inheritdoc />
    public partial class AddStoryImageAvatarCrop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AvatarFocusXPercent",
                table: "StoryImageAssets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AvatarFocusYPercent",
                table: "StoryImageAssets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AvatarZoomPercent",
                table: "StoryImageAssets",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarFocusXPercent",
                table: "StoryImageAssets");

            migrationBuilder.DropColumn(
                name: "AvatarFocusYPercent",
                table: "StoryImageAssets");

            migrationBuilder.DropColumn(
                name: "AvatarZoomPercent",
                table: "StoryImageAssets");
        }
    }
}
