using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmsPanel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReconciliationIssueMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReconciliationIssueMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReconciliationIssueId = table.Column<long>(type: "bigint", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SentBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationIssueMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReconciliationIssueMessages_ReconciliationIssues_ReconciliationIssueId",
                        column: x => x.ReconciliationIssueId,
                        principalTable: "ReconciliationIssues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationIssueMessages_ReconciliationIssueId",
                table: "ReconciliationIssueMessages",
                column: "ReconciliationIssueId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReconciliationIssueMessages");
        }
    }
}
