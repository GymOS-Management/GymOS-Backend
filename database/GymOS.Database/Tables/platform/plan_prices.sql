/*
    [platform].[plan_prices]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[plan_prices] (

    [id] uniqueidentifier NOT NULL,
    [plan_id] uniqueidentifier NOT NULL,
    [billing_cycle] smallint NOT NULL,
    [price_amount] decimal(14,4) NOT NULL,
    [price_currency] nvarchar(3) NOT NULL,
    CONSTRAINT [pk_plan_prices] PRIMARY KEY ([id]),
    CONSTRAINT [fk_plan_prices_subscription_plans_plan_id] FOREIGN KEY ([plan_id]) REFERENCES [platform].[subscription_plans] ([id]) ON DELETE CASCADE
);


GO

CREATE INDEX [ix_plan_prices_plan_id_billing_cycle]
    ON [platform].[plan_prices] ([plan_id], [billing_cycle]);

GO

CREATE UNIQUE INDEX [ix_plan_prices_plan_cycle_currency]
    ON [platform].[plan_prices] (plan_id, billing_cycle, price_currency);
