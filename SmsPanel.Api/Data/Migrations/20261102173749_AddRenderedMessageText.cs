using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmsPanel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRenderedMessageText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RenderedText",
                table: "OutboundMessages",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RenderedText",
                table: "OutboundMessages");
        }
    }
}
