/*
    [platform].[subscription_plans]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[subscription_plans] (

    [id] uniqueidentifier NOT NULL,
    [code] nvarchar(40) NOT NULL,
    [name] nvarchar(120) NOT NULL,
    [description] nvarchar(2000) NOT NULL,
    [trial_days] int NOT NULL,
    [max_members] int NULL,
    [max_trainers] int NULL,
    [max_staff] int NULL,
    [storage_limit_mb] bigint NULL,
    [is_active] bit NOT NULL,
    [is_public] bit NOT NULL,
    [sort_order] int NOT NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    [updated_at_utc] datetimeoffset NOT NULL,
    [row_version] rowversion NOT NULL,
    CONSTRAINT [pk_subscription_plans] PRIMARY KEY ([id]),
    CONSTRAINT [ck_subscription_plans_code_upper] CHECK (code = upper(code))
);


GO

CREATE UNIQUE INDEX [ix_subscription_plans_code]
    ON [platform].[subscription_plans] ([code]);

GO

CREATE INDEX [ix_subscription_plans_is_active_sort_order]
    ON [platform].[subscription_plans] ([is_active], [sort_order]);
