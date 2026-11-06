BEGIN TRANSACTION;
IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20261106074000_AddInboundMatchedPerson')
BEGIN
    ALTER TABLE [InboundMessages] ADD [MatchedPersonId] bigint NULL;
    CREATE INDEX [IX_InboundMessages_MatchedPersonId] ON [InboundMessages] ([MatchedPersonId]);
    ALTER TABLE [InboundMessages] ADD CONSTRAINT [FK_InboundMessages_People_MatchedPersonId]
        FOREIGN KEY ([MatchedPersonId]) REFERENCES [People] ([Id]) ON DELETE SET NULL;
END;

IF NOT EXISTS (SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20261106074000_AddInboundMatchedPerson')
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'20261106074000_AddInboundMatchedPerson', N'10.0.14');
END;
COMMIT;
GO
