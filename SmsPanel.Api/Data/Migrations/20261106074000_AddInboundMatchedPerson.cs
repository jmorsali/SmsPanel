using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmsPanel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInboundMatchedPerson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MatchedPersonId",
                table: "InboundMessages",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessages_MatchedPersonId",
                table: "InboundMessages",
                column: "MatchedPersonId");

            migrationBuilder.AddForeignKey(
                name: "FK_InboundMessages_People_MatchedPersonId",
                table: "InboundMessages",
                column: "MatchedPersonId",
                principalTable: "People",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InboundMessages_People_MatchedPersonId",
                table: "InboundMessages");

            migrationBuilder.DropIndex(
                name: "IX_InboundMessages_MatchedPersonId",
                table: "InboundMessages");

            migrationBuilder.DropColumn(
                name: "MatchedPersonId",
                table: "InboundMessages");
        }
    }
}
