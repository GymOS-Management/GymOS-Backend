/*
    [platform].[idempotency_records]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[idempotency_records] (

    [id] uniqueidentifier NOT NULL,
    [tenant_id] uniqueidentifier NULL,
    [operation_key] nvarchar(100) NOT NULL,
    [idempotency_key] nvarchar(200) NOT NULL,
    [request_hash] nchar(64) NOT NULL,
    [claim_token] uniqueidentifier NOT NULL,
    [status] int NOT NULL,
    [response_status] int NOT NULL,
    [response_body] nvarchar(max) NULL,
    [lease_expires_at] datetimeoffset NOT NULL,
    [created_at] datetimeoffset NOT NULL,
    [expires_at] datetimeoffset NOT NULL,
    CONSTRAINT [pk_idempotency_records] PRIMARY KEY ([id])
);


GO

CREATE INDEX [ix_idempotency_records_expires_at]
    ON [platform].[idempotency_records] ([expires_at]);

GO

CREATE UNIQUE INDEX [ux_idempotency_records_claim]
    ON [platform].[idempotency_records] (tenant_id, operation_key, idempotency_key);
