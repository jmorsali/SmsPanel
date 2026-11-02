using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmsPanel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantMobile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SourceImportBatchId",
                table: "People",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mobile",
                table: "Merchants",
                type: "nvarchar(11)",
                maxLength: 11,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_People_SourceImportBatchId",
                table: "People",
                column: "SourceImportBatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_People_SourceImportBatchId",
                table: "People");

            migrationBuilder.DropColumn(
                name: "SourceImportBatchId",
                table: "People");

            migrationBuilder.DropColumn(
                name: "Mobile",
                table: "Merchants");
        }
    }
}
