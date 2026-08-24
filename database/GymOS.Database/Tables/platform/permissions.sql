/*
    [platform].[permissions]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[permissions] (

    [id] uniqueidentifier NOT NULL,
    [key] nvarchar(100) NOT NULL,
    [module] nvarchar(50) NOT NULL,
    [description] nvarchar(500) NOT NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    [updated_at_utc] datetimeoffset NOT NULL,
    CONSTRAINT [pk_permissions] PRIMARY KEY ([id])
);


GO

CREATE UNIQUE INDEX [ix_permissions_key]
    ON [platform].[permissions] ([key]);
