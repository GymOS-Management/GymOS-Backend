/*
    [platform].[role_permissions]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[role_permissions] (

    [role_id] uniqueidentifier NOT NULL,
    [permission_id] uniqueidentifier NOT NULL,
    CONSTRAINT [pk_role_permissions] PRIMARY KEY ([role_id], [permission_id]),
    CONSTRAINT [fk_role_permissions_permissions_permission_id] FOREIGN KEY ([permission_id]) REFERENCES [platform].[permissions] ([id]) ON DELETE CASCADE,
    CONSTRAINT [fk_role_permissions_roles_role_id] FOREIGN KEY ([role_id]) REFERENCES [platform].[roles] ([id]) ON DELETE CASCADE
);


GO

CREATE INDEX [ix_role_permissions_permission_id]
    ON [platform].[role_permissions] ([permission_id]);
