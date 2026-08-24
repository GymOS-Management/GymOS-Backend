/*
    Partition scheme [ps_audit_logs_monthly]

    ALL TO ([PRIMARY]) — one filegroup, as the migration creates it. The scheme is stable even
    though the function it partitions by is not, because ALL TO means adding a range needs no new
    filegroup. See pf_audit_logs_monthly.sql for why that function is not comparable.
*/
CREATE PARTITION SCHEME [ps_audit_logs_monthly]
    AS PARTITION [pf_audit_logs_monthly] ALL TO ([PRIMARY]);
