/*
    [platform].[roles]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[roles] (

    [id] uniqueidentifier NOT NULL,
    [name] nvarchar(100) NOT NULL,
    [normalized_name] nvarchar(100) NOT NULL,
    [description] nvarchar(500) NOT NULL,
    [scope] smallint NOT NULL,
    [tenant_id] uniqueidentifier NULL,
    [is_system] bit NOT NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    [updated_at_utc] datetimeoffset NOT NULL,
    [row_version] rowversion NOT NULL,
    CONSTRAINT [pk_roles] PRIMARY KEY ([id])
);


GO

CREATE UNIQUE INDEX [ix_roles_normalized_name_tenant_id]
    ON [platform].[roles] ([normalized_name], [tenant_id]);
