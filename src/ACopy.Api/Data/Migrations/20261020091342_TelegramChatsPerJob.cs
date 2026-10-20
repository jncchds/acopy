using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ACopy.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TelegramChatsPerJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TelegramChatIds",
                table: "Targets",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TelegramChatIds",
                table: "Targets");
        }
    }
}
