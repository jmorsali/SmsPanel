using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmsPanel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignInvalidReply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InvalidReplyEnabled",
                table: "Campaigns",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "InvalidReplyTemplate",
                table: "Campaigns",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvalidReplyEnabled",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "InvalidReplyTemplate",
                table: "Campaigns");
        }
    }
}
