/*
    Partition function [pf_audit_logs_monthly]

    ** THE BOUNDARY LIST BELOW IS A SNAPSHOT AND IS EXPECTED TO BE WRONG. **

    This object cannot be represented declaratively, and the reason is worth stating precisely.
    The migration creates it with a boundary derived from SYSUTCDATETIME() at the moment it runs,
    and platform.ensure_audit_log_partitions then SPLITs a further boundary every month for the
    life of the database. Its range list is therefore runtime state, not schema: two databases
    migrated on different days disagree, and every database disagrees with this file the month
    after it is written.

    It is declared here only because it has to be. The audit_logs primary key and four of its
    indexes are stored ON ps_audit_logs_monthly, that scheme requires this function, and a project
    missing it does not build.

    ** Never publish this project to a database. ** Reconciling a live boundary list against this
    one means MERGE RANGE, which moves audit rows between partitions and can destroy them. The
    comparison tooling passes /p:IgnorePartitionSchemes=True precisely so this object is never
    diffed; database/README.md explains the workflow that depends on it.

    The values below are what a database migrated on 2026-08-24 has: the current month, plus the
    three the migration's closing call to ensure_audit_log_partitions creates.
*/
CREATE PARTITION FUNCTION [pf_audit_logs_monthly] (datetimeoffset(7))
    AS RANGE RIGHT FOR VALUES (
        '2026-08-01T00:00:00.0000000+00:00',
        '2026-09-01T00:00:00.0000000+00:00',
        '2026-10-01T00:00:00.0000000+00:00',
        '2026-11-01T00:00:00.0000000+00:00'
    );
