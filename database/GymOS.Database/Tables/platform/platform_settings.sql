/*
    [platform].[platform_settings]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[platform_settings] (

    [id] uniqueidentifier NOT NULL,
    [key] nvarchar(100) NOT NULL,
    [category] tinyint NOT NULL,
    [value_type] tinyint NOT NULL,
    [is_sensitive] bit NOT NULL,
    [value] nvarchar(max) NULL,
    [protected_value] nvarchar(max) NULL,
    [description] nvarchar(500) NOT NULL,
    [updated_by] uniqueidentifier NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    [updated_at_utc] datetimeoffset NOT NULL,
    [row_version] rowversion NOT NULL,
    CONSTRAINT [pk_platform_settings] PRIMARY KEY ([id]),
    CONSTRAINT [ck_platform_settings_one_value] CHECK (([value] IS NULL AND [protected_value] IS NOT NULL) OR ([value] IS NOT NULL AND [protected_value] IS NULL))
);


GO

CREATE UNIQUE INDEX [ux_platform_settings_key]
    ON [platform].[platform_settings] ([key]);
