using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRp.Migrations
{
    [DbContext(typeof(AppContext))]
    [Migration("20260425064234_AddChatMessagePrivateIntent")]
    public partial class AddChatMessagePrivateIntent : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.ChatMessages', 'PrivateIntent') IS NULL
                    ALTER TABLE [ChatMessages] ADD [PrivateIntent] nvarchar(max) NULL;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.ChatMessages', 'PrivateIntent') IS NOT NULL
                    ALTER TABLE [ChatMessages] DROP COLUMN [PrivateIntent];
                """);
        }
    }
}
