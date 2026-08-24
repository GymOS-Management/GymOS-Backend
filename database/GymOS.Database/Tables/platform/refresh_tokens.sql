/*
    [platform].[refresh_tokens]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[refresh_tokens] (

    [id] uniqueidentifier NOT NULL,
    [user_id] uniqueidentifier NOT NULL,
    [token_hash] nvarchar(128) NOT NULL,
    [expires_at_utc] datetimeoffset NOT NULL,
    [created_by_ip] nvarchar(45) NOT NULL,
    [revoked_at_utc] datetimeoffset NULL,
    [revoked_by_ip] nvarchar(45) NULL,
    [revocation_reason] nvarchar(200) NULL,
    [replaced_by_token_hash] nvarchar(128) NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    [updated_at_utc] datetimeoffset NOT NULL,
    CONSTRAINT [pk_refresh_tokens] PRIMARY KEY ([id]),
    CONSTRAINT [fk_refresh_tokens_user_user_id] FOREIGN KEY ([user_id]) REFERENCES [platform].[users] ([id]) ON DELETE CASCADE
);


GO

CREATE UNIQUE INDEX [ix_refresh_tokens_token_hash]
    ON [platform].[refresh_tokens] ([token_hash]);

GO

CREATE INDEX [ix_refresh_tokens_user_id_expires_at_utc]
    ON [platform].[refresh_tokens] ([user_id], [expires_at_utc]);
