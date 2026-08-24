/*
    [platform].[daily_platform_stats_mrr]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[daily_platform_stats_mrr] (

    [id] uniqueidentifier NOT NULL,
    [stats_id] uniqueidentifier NOT NULL,
    [amount_amount] decimal(14,4) NOT NULL,
    [amount_currency] nvarchar(3) NOT NULL,
    CONSTRAINT [pk_daily_platform_stats_mrr] PRIMARY KEY ([id]),
    CONSTRAINT [fk_daily_platform_stats_mrr_daily_platform_stats_stats_id] FOREIGN KEY ([stats_id]) REFERENCES [platform].[daily_platform_stats] ([id]) ON DELETE CASCADE
);


GO

CREATE INDEX [ix_daily_platform_stats_mrr_stats_id]
    ON [platform].[daily_platform_stats_mrr] ([stats_id]);

GO

CREATE UNIQUE INDEX [ux_daily_platform_stats_mrr_currency]
    ON [platform].[daily_platform_stats_mrr] (stats_id, amount_currency);
