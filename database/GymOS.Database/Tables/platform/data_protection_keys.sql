/*
    [platform].[data_protection_keys]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[data_protection_keys] (

    [id] int NOT NULL IDENTITY,
    [friendly_name] nvarchar(200) NULL,
    [xml] nvarchar(max) NULL,
    CONSTRAINT [pk_data_protection_keys] PRIMARY KEY ([id])
);
