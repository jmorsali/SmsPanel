using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmsPanel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeOwnershipVerificationNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "OwnershipVerified",
                table: "SecondaryBankAccounts",
                type: "bit",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "bit");

            migrationBuilder.Sql("UPDATE [SecondaryBankAccounts] SET [OwnershipVerified] = NULL WHERE [OwnershipVerified] = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "OwnershipVerified",
                table: "SecondaryBankAccounts",
                type: "bit",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldNullable: true);
        }
    }
}
