/*
    [platform].[invoice_lines]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[invoice_lines] (

    [id] uniqueidentifier NOT NULL,
    [invoice_id] uniqueidentifier NOT NULL,
    [description] nvarchar(500) NOT NULL,
    [quantity] int NOT NULL,
    [amount_amount] decimal(14,4) NOT NULL,
    [amount_currency] nvarchar(3) NOT NULL,
    [unit_price_amount] decimal(14,4) NOT NULL,
    [unit_price_currency] nvarchar(3) NOT NULL,
    CONSTRAINT [pk_invoice_lines] PRIMARY KEY ([id]),
    CONSTRAINT [fk_invoice_lines_invoices_invoice_id] FOREIGN KEY ([invoice_id]) REFERENCES [platform].[invoices] ([id]) ON DELETE CASCADE
);


GO

CREATE INDEX [ix_invoice_lines_invoice_id]
    ON [platform].[invoice_lines] ([invoice_id]);
