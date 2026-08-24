/*
    [platform].[gym_branding]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[gym_branding] (

    [id] uniqueidentifier NOT NULL,
    [gym_id] uniqueidentifier NOT NULL,
    [logo_url] nvarchar(500) NULL,
    [favicon_url] nvarchar(500) NULL,
    [primary_colour] nvarchar(7) NULL,
    [secondary_colour] nvarchar(7) NULL,
    [custom_domain] nvarchar(253) NULL,
    [email_sender_name] nvarchar(100) NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    [updated_at_utc] datetimeoffset NOT NULL,
    [row_version] rowversion NOT NULL,
    CONSTRAINT [pk_gym_branding] PRIMARY KEY ([id]),
    CONSTRAINT [fk_gym_branding_gym_gym_id] FOREIGN KEY ([gym_id]) REFERENCES [platform].[gyms] ([id]) ON DELETE CASCADE
);


GO

CREATE UNIQUE INDEX [ix_gym_branding_custom_domain]
    ON [platform].[gym_branding] ([custom_domain]) WHERE custom_domain IS NOT NULL;

GO

CREATE UNIQUE INDEX [ix_gym_branding_gym_id]
    ON [platform].[gym_branding] ([gym_id]);
