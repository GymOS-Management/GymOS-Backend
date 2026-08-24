/*
    [outbox].[deliveries]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [outbox].[deliveries] (

    [id] uniqueidentifier NOT NULL,
    [message_id] uniqueidentifier NOT NULL,
    [handler_name] nvarchar(400) NOT NULL,
    [status] smallint NOT NULL,
    [attempts] int NOT NULL,
    [next_retry_at_utc] datetimeoffset NULL,
    [last_error] nvarchar(4000) NULL,
    [processed_at_utc] datetimeoffset NULL,
    CONSTRAINT [pk_deliveries] PRIMARY KEY ([id]),
    CONSTRAINT [fk_deliveries_outbox_message_message_id] FOREIGN KEY ([message_id]) REFERENCES [outbox].[messages] ([id]) ON DELETE CASCADE
);


GO

CREATE UNIQUE INDEX [ix_deliveries_message_id_handler_name]
    ON [outbox].[deliveries] ([message_id], [handler_name]);

GO

CREATE INDEX [ix_deliveries_status_next_retry_at_utc]
    ON [outbox].[deliveries] ([status], [next_retry_at_utc]);
