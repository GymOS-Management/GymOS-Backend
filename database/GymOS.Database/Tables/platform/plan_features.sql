/*
    [platform].[plan_features]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[plan_features] (

    [id] uniqueidentifier NOT NULL,
    [plan_id] uniqueidentifier NOT NULL,
    [feature_id] uniqueidentifier NOT NULL,
    [is_enabled] bit NOT NULL,
    CONSTRAINT [pk_plan_features] PRIMARY KEY ([id]),
    CONSTRAINT [fk_plan_features_features_feature_id] FOREIGN KEY ([feature_id]) REFERENCES [platform].[features] ([id]) ON DELETE NO ACTION,
    CONSTRAINT [fk_plan_features_subscription_plans_plan_id] FOREIGN KEY ([plan_id]) REFERENCES [platform].[subscription_plans] ([id]) ON DELETE CASCADE
);


GO

CREATE INDEX [ix_plan_features_feature_id]
    ON [platform].[plan_features] ([feature_id]);

GO

CREATE UNIQUE INDEX [ux_plan_features_plan_feature]
    ON [platform].[plan_features] ([plan_id], [feature_id]);
