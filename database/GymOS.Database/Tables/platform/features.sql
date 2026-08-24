/*
    [platform].[features]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[features] (

    [id] uniqueidentifier NOT NULL,
    [key] nvarchar(60) NOT NULL,
    [name] nvarchar(120) NOT NULL,
    [description] nvarchar(1000) NOT NULL,
    [category] nvarchar(60) NOT NULL,
    [default_enabled] bit NOT NULL,
    [is_premium] bit NOT NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    [updated_at_utc] datetimeoffset NOT NULL,
    [row_version] rowversion NOT NULL,
    CONSTRAINT [pk_features] PRIMARY KEY ([id])
);


GO

CREATE INDEX [ix_features_category_name]
    ON [platform].[features] ([category], [name]);

GO

CREATE UNIQUE INDEX [ux_features_key]
    ON [platform].[features] ([key]);
