/*
    [platform].[gym_feature_overrides]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[gym_feature_overrides] (

    [id] uniqueidentifier NOT NULL,
    [gym_id] uniqueidentifier NOT NULL,
    [feature_id] uniqueidentifier NOT NULL,
    [is_enabled] bit NOT NULL,
    [reason] nvarchar(500) NOT NULL,
    [set_by_user_id] uniqueidentifier NULL,
    [set_at_utc] datetimeoffset NOT NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    [updated_at_utc] datetimeoffset NOT NULL,
    [row_version] rowversion NOT NULL,
    CONSTRAINT [pk_gym_feature_overrides] PRIMARY KEY ([id]),
    CONSTRAINT [fk_gym_feature_overrides_features_feature_id] FOREIGN KEY ([feature_id]) REFERENCES [platform].[features] ([id]) ON DELETE NO ACTION,
    CONSTRAINT [fk_gym_feature_overrides_gyms_gym_id] FOREIGN KEY ([gym_id]) REFERENCES [platform].[gyms] ([id]) ON DELETE CASCADE,
    CONSTRAINT [fk_gym_feature_overrides_user_set_by_user_id] FOREIGN KEY ([set_by_user_id]) REFERENCES [platform].[users] ([id]) ON DELETE SET NULL
);


GO

CREATE INDEX [ix_gym_feature_overrides_feature_id]
    ON [platform].[gym_feature_overrides] ([feature_id]);

GO

CREATE INDEX [ix_gym_feature_overrides_gym_id]
    ON [platform].[gym_feature_overrides] ([gym_id]);

GO

CREATE INDEX [ix_gym_feature_overrides_set_by_user_id]
    ON [platform].[gym_feature_overrides] ([set_by_user_id]);

GO

CREATE UNIQUE INDEX [ux_gym_feature_overrides_gym_feature]
    ON [platform].[gym_feature_overrides] ([gym_id], [feature_id]);
