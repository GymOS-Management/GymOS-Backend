/*
    [platform].[invoices]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[invoices] (

    [id] uniqueidentifier NOT NULL,
    [gym_id] uniqueidentifier NOT NULL,
    [subscription_id] uniqueidentifier NOT NULL,
    [invoice_number] nvarchar(20) NOT NULL,
    [status] smallint NOT NULL,
    [tax_rate] decimal(5,4) NOT NULL,
    [issued_at_utc] datetimeoffset NOT NULL,
    [due_at_utc] datetimeoffset NOT NULL,
    [paid_at_utc] datetimeoffset NULL,
    [payment_reference] nvarchar(200) NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    [updated_at_utc] datetimeoffset NOT NULL,
    [row_version] rowversion NOT NULL,
    [subtotal_amount] decimal(14,4) NOT NULL,
    [subtotal_currency] nvarchar(3) NOT NULL,
    [tax_amount] decimal(14,4) NOT NULL,
    [tax_currency] nvarchar(3) NOT NULL,
    [total_amount] decimal(14,4) NOT NULL,
    [total_currency] nvarchar(3) NOT NULL,
    CONSTRAINT [pk_invoices] PRIMARY KEY ([id]),
    CONSTRAINT [fk_invoices_gym_subscriptions_subscription_id] FOREIGN KEY ([subscription_id]) REFERENCES [platform].[gym_subscriptions] ([id]) ON DELETE NO ACTION,
    CONSTRAINT [fk_invoices_gyms_gym_id] FOREIGN KEY ([gym_id]) REFERENCES [platform].[gyms] ([id]) ON DELETE NO ACTION,
    CONSTRAINT [ck_invoices_single_currency] CHECK (subtotal_currency = tax_currency AND tax_currency = total_currency)
);


GO

CREATE INDEX [ix_invoices_gym_id_created_at_utc]
    ON [platform].[invoices] ([gym_id], [created_at_utc]);

GO

CREATE INDEX [ix_invoices_status_due_at_utc]
    ON [platform].[invoices] ([status], [due_at_utc]);

GO

CREATE INDEX [ix_invoices_subscription_id]
    ON [platform].[invoices] ([subscription_id]);

GO

CREATE UNIQUE INDEX [ux_invoices_invoice_number]
    ON [platform].[invoices] ([invoice_number]);
