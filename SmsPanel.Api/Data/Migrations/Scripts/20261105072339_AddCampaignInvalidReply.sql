BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261105072339_AddCampaignInvalidReply'
)
BEGIN
    ALTER TABLE [Campaigns] ADD [InvalidReplyEnabled] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261105072339_AddCampaignInvalidReply'
)
BEGIN
    ALTER TABLE [Campaigns] ADD [InvalidReplyTemplate] nvarchar(1000) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261105072339_AddCampaignInvalidReply'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261105072339_AddCampaignInvalidReply', N'10.0.14');
END;

COMMIT;
GO

