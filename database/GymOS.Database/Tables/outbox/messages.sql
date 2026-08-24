/*
    [outbox].[messages]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [outbox].[messages] (

    [id] uniqueidentifier NOT NULL,
    [event_id] uniqueidentifier NOT NULL,
    [event_name] nvarchar(200) NOT NULL,
    [event_version] int NOT NULL,
    [occurred_at_utc] datetimeoffset NOT NULL,
    [tenant_id] uniqueidentifier NULL,
    [aggregate_type] nvarchar(200) NOT NULL,
    [aggregate_id] uniqueidentifier NOT NULL,
    [user_id] uniqueidentifier NULL,
    [correlation_id] uniqueidentifier NULL,
    [causation_depth] int NOT NULL,
    [payload] nvarchar(max) NOT NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    CONSTRAINT [pk_messages] PRIMARY KEY ([id])
);


GO

CREATE INDEX [ix_messages_created_at_utc]
    ON [outbox].[messages] ([created_at_utc]);

GO

CREATE UNIQUE INDEX [ix_messages_event_id]
    ON [outbox].[messages] ([event_id]);

GO

CREATE INDEX [ix_messages_event_name]
    ON [outbox].[messages] ([event_name]);
