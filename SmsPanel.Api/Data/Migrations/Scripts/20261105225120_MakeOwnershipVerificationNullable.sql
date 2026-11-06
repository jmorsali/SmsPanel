BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261105225120_MakeOwnershipVerificationNullable'
)
BEGIN
    ALTER TABLE [SecondaryBankAccounts] ALTER COLUMN [OwnershipVerified] bit NULL;
    UPDATE [SecondaryBankAccounts] SET [OwnershipVerified] = NULL WHERE [OwnershipVerified] = 0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261105225120_MakeOwnershipVerificationNullable'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261105225120_MakeOwnershipVerificationNullable', N'10.0.14');
END;

COMMIT;
GO
