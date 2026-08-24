/*
    [platform].[gym_subscriptions]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[gym_subscriptions] (

    [id] uniqueidentifier NOT NULL,
    [gym_id] uniqueidentifier NOT NULL,
    [plan_id] uniqueidentifier NOT NULL,
    [plan_price_id] uniqueidentifier NULL,
    [billing_cycle] smallint NOT NULL,
    [status] smallint NOT NULL,
    [current_period_start_utc] datetimeoffset NOT NULL,
    [current_period_end_utc] datetimeoffset NOT NULL,
    [period_anchor_utc] datetimeoffset NOT NULL,
    [trial_ends_at_utc] datetimeoffset NULL,
    [canceled_at_utc] datetimeoffset NULL,
    [expired_at_utc] datetimeoffset NULL,
    [cancel_at_period_end] bit NOT NULL,
    [auto_renew] bit NOT NULL,
    [cancellation_reason] nvarchar(1000) NULL,
    [pending_plan_id] uniqueidentifier NULL,
    [pending_plan_price_id] uniqueidentifier NULL,
    [pending_billing_cycle] smallint NULL,
    [pending_price_amount] decimal(14,4) NULL,
    [pending_price_currency] nvarchar(3) NULL,
    [pending_change_at_utc] datetimeoffset NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    [updated_at_utc] datetimeoffset NOT NULL,
    [row_version] rowversion NOT NULL,
    [price_amount] decimal(14,4) NOT NULL,
    [price_currency] nvarchar(3) NOT NULL,
    CONSTRAINT [pk_gym_subscriptions] PRIMARY KEY ([id]),
    CONSTRAINT [fk_gym_subscriptions_gyms_gym_id] FOREIGN KEY ([gym_id]) REFERENCES [platform].[gyms] ([id]) ON DELETE NO ACTION,
    CONSTRAINT [fk_gym_subscriptions_plan_price_plan_price_id] FOREIGN KEY ([plan_price_id]) REFERENCES [platform].[plan_prices] ([id]) ON DELETE SET NULL,
    CONSTRAINT [fk_gym_subscriptions_subscription_plan_plan_id] FOREIGN KEY ([plan_id]) REFERENCES [platform].[subscription_plans] ([id]) ON DELETE NO ACTION
);


GO

CREATE INDEX [ix_gym_subscriptions_canceled_at_utc]
    ON [platform].[gym_subscriptions] ([canceled_at_utc]) WHERE status = 4;

GO

CREATE INDEX [ix_gym_subscriptions_expired_at_utc]
    ON [platform].[gym_subscriptions] ([expired_at_utc]) WHERE status = 5;

GO

CREATE INDEX [ix_gym_subscriptions_gym_id_created_at_utc]
    ON [platform].[gym_subscriptions] ([gym_id], [created_at_utc]);

GO

CREATE INDEX [ix_gym_subscriptions_plan_id]
    ON [platform].[gym_subscriptions] ([plan_id]);

GO

CREATE INDEX [ix_gym_subscriptions_plan_price_id]
    ON [platform].[gym_subscriptions] ([plan_price_id]);

GO

CREATE INDEX [ix_gym_subscriptions_status_current_period_end_utc]
    ON [platform].[gym_subscriptions] ([status], [current_period_end_utc]);

GO

CREATE UNIQUE INDEX [ux_gym_subscriptions_one_live_per_gym]
    ON [platform].[gym_subscriptions] ([gym_id]) WHERE status IN (1, 2, 3);
