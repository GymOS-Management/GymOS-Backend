/*
    [platform].[audit_logs]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[audit_logs] (

    [id] uniqueidentifier NOT NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    [user_id] uniqueidentifier NULL,
    [user_email] nvarchar(320) NULL,
    [ip_address] nvarchar(45) NULL,
    [user_agent] nvarchar(512) NULL,
    [action] nvarchar(100) NOT NULL,
    [entity_type] nvarchar(100) NULL,
    [entity_id] uniqueidentifier NULL,
    [old_values] nvarchar(max) NULL,
    [new_values] nvarchar(max) NULL,
    [correlation_id] uniqueidentifier NULL,
    CONSTRAINT [pk_audit_logs] PRIMARY KEY CLUSTERED ([id], [created_at_utc])
        ON [ps_audit_logs_monthly] ([created_at_utc])
);


GO

CREATE INDEX [ix_audit_logs_action]
    ON [platform].[audit_logs] (action, created_at_utc) ON ps_audit_logs_monthly (created_at_utc);

GO

CREATE INDEX [ix_audit_logs_created_at_utc]
    ON [platform].[audit_logs] (created_at_utc DESC) ON ps_audit_logs_monthly (created_at_utc);

GO

CREATE INDEX [ix_audit_logs_entity_type_entity_id]
    ON [platform].[audit_logs] (entity_type, entity_id, created_at_utc) ON ps_audit_logs_monthly (created_at_utc);

GO

CREATE INDEX [ix_audit_logs_user_id]
    ON [platform].[audit_logs] (user_id, created_at_utc) ON ps_audit_logs_monthly (created_at_utc);
