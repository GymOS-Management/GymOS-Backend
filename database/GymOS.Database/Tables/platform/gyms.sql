/*
    [platform].[gyms]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[gyms] (

    [id] uniqueidentifier NOT NULL,
    [slug] nvarchar(63) NOT NULL,
    [name] nvarchar(200) NOT NULL,
    [status] smallint NOT NULL,
    [suspension_reason] nvarchar(1000) NULL,
    [owner_name] nvarchar(200) NOT NULL,
    [owner_email] nvarchar(320) NOT NULL,
    [phone] nvarchar(40) NULL,
    [country_code] nvarchar(2) NOT NULL,
    [time_zone_id] nvarchar(100) NOT NULL,
    [currency] nvarchar(3) NOT NULL,
    [deleted_at_utc] datetimeoffset NULL,
    [created_at_utc] datetimeoffset NOT NULL,
    [updated_at_utc] datetimeoffset NOT NULL,
    [row_version] rowversion NOT NULL,
    CONSTRAINT [pk_gyms] PRIMARY KEY ([id]),
    CONSTRAINT [ck_gyms_slug_lowercase] CHECK (slug = lower(slug))
);


GO

CREATE UNIQUE INDEX [ix_gyms_slug]
    ON [platform].[gyms] ([slug]);

GO

CREATE INDEX [ix_gyms_status_created_at_utc]
    ON [platform].[gyms] ([status], [created_at_utc]);
