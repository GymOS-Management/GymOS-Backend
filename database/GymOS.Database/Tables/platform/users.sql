/*
    [platform].[users]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[users] (

    [id] uniqueidentifier NOT NULL,
    [email] nvarchar(320) NOT NULL,
    [normalized_email] nvarchar(320) NOT NULL,
    [password_hash] nvarchar(500) NULL,
    [first_name] nvarchar(100) NOT NULL,
    [last_name] nvarchar(100) NOT NULL,
    [status] smallint NOT NULL,
    [suspension_reason] nvarchar(1000) NULL,
    [failed_login_attempts] int NOT NULL,
    [locked_out_until_utc] datetimeoffset NULL,
    [last_login_at_utc] datetimeoffset NULL,
    [password_changed_at_utc] datetimeoffset NULL,
    [security_stamp] uniqueidentifier NOT NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    [updated_at_utc] datetimeoffset NOT NULL,
    [row_version] rowversion NOT NULL,
    CONSTRAINT [pk_users] PRIMARY KEY ([id])
);


GO

CREATE UNIQUE INDEX [ix_users_normalized_email]
    ON [platform].[users] ([normalized_email]);
