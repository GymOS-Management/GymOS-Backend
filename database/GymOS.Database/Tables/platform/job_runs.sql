/*
    [platform].[job_runs]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[job_runs] (

    [id] uniqueidentifier NOT NULL,
    [job_name] nvarchar(100) NOT NULL,
    [instance] nvarchar(100) NOT NULL,
    [status] tinyint NOT NULL,
    [started_at_utc] datetimeoffset NOT NULL,
    [finished_at_utc] datetimeoffset NULL,
    [duration_ms] bigint NULL,
    [error] nvarchar(2000) NULL,
    CONSTRAINT [pk_job_runs] PRIMARY KEY ([id])
);


GO

CREATE INDEX [ix_job_runs_name_started]
    ON [platform].[job_runs] ([job_name], [started_at_utc] DESC);
