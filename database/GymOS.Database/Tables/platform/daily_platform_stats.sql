/*
    [platform].[daily_platform_stats]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[daily_platform_stats] (

    [id] uniqueidentifier NOT NULL,
    [stat_date] date NOT NULL,
    [total_gyms] int NOT NULL,
    [active_gyms] int NOT NULL,
    [suspended_gyms] int NOT NULL,
    [provisioning_gyms] int NOT NULL,
    [new_gyms] int NOT NULL,
    [canceled_gyms] int NOT NULL,
    [expired_gyms] int NOT NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    CONSTRAINT [pk_daily_platform_stats] PRIMARY KEY ([id])
);


GO

CREATE UNIQUE INDEX [ux_daily_platform_stats_date]
    ON [platform].[daily_platform_stats] ([stat_date]);
