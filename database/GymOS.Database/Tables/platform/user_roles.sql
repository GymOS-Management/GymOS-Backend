/*
    [platform].[user_roles]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[user_roles] (

    [id] uniqueidentifier NOT NULL,
    [user_id] uniqueidentifier NOT NULL,
    [role_id] uniqueidentifier NOT NULL,
    [tenant_id] uniqueidentifier NULL,
    CONSTRAINT [pk_user_roles] PRIMARY KEY ([id]),
    CONSTRAINT [fk_user_roles_roles_role_id] FOREIGN KEY ([role_id]) REFERENCES [platform].[roles] ([id]) ON DELETE CASCADE,
    CONSTRAINT [fk_user_roles_users_user_id] FOREIGN KEY ([user_id]) REFERENCES [platform].[users] ([id]) ON DELETE CASCADE
);


GO

CREATE INDEX [ix_user_roles_role_id]
    ON [platform].[user_roles] ([role_id]);

GO

CREATE UNIQUE INDEX [ix_user_roles_user_id_role_id_tenant_id]
    ON [platform].[user_roles] ([user_id], [role_id], [tenant_id]);
