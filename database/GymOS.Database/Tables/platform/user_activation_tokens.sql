/*
    [platform].[user_activation_tokens]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[user_activation_tokens] (

    [id] uniqueidentifier NOT NULL,
    [user_id] uniqueidentifier NOT NULL,
    [token_hash] nvarchar(128) NOT NULL,
    [purpose] int NOT NULL,
    [expires_at_utc] datetimeoffset NOT NULL,
    [consumed_at_utc] datetimeoffset NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    [created_by_user_id] uniqueidentifier NULL,
    [row_version] rowversion NOT NULL,
    CONSTRAINT [pk_user_activation_tokens] PRIMARY KEY ([id]),
    CONSTRAINT [fk_user_activation_tokens_user_created_by_user_id] FOREIGN KEY ([created_by_user_id]) REFERENCES [platform].[users] ([id]),
    CONSTRAINT [fk_user_activation_tokens_user_user_id] FOREIGN KEY ([user_id]) REFERENCES [platform].[users] ([id]) ON DELETE CASCADE
);


GO

CREATE INDEX [ix_user_activation_tokens_created_by_user_id]
    ON [platform].[user_activation_tokens] ([created_by_user_id]);

GO

CREATE INDEX [ix_user_activation_tokens_expires_at_utc]
    ON [platform].[user_activation_tokens] ([expires_at_utc]);

GO

CREATE UNIQUE INDEX [ix_user_activation_tokens_token_hash]
    ON [platform].[user_activation_tokens] ([token_hash]);

GO

CREATE INDEX [ix_user_activation_tokens_user_id_consumed_at_utc]
    ON [platform].[user_activation_tokens] ([user_id], [consumed_at_utc]);
