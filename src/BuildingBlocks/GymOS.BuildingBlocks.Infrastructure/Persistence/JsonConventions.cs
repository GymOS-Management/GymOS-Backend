namespace GymOS.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// How GymOS stores a JSON document column: the outbox payload, the audit trail's before/after
/// snapshots, and a replayed idempotent response body.
///
/// <para>Centralized for the same reason <see cref="MoneyConventions"/> is: three assemblies map a
/// JSON column and they have to agree, or a document written by one is a different type to another.
/// </para>
/// </summary>
public static class JsonConventions
{
    /// <summary>
    /// <c>nvarchar(max)</c> — the JSON document as text.
    ///
    /// <para><b>Not a native JSON type, deliberately.</b> PostgreSQL stored these as <c>jsonb</c>, a
    /// parsed binary form with its own operators and index support. SQL Server's nearest equivalent,
    /// the <c>json</c> type, exists only in SQL Server 2025 and current Azure SQL Database — not in
    /// the 2022 image this repository develops and tests against, and not in any earlier on-premises
    /// version an operator might already be running. Choosing it would make the schema
    /// version-dependent for no behaviour anyone currently uses: nothing in GymOS queries inside these
    /// documents. The outbox payload is deserialized by the processor, the audit snapshots are passed
    /// through to the console's diff viewer verbatim, and a stored idempotent response is written back
    /// to the wire unread.</para>
    ///
    /// <para><b>What is given up, and what to do when it is needed.</b> No server-side validation that
    /// the text is well-formed JSON, and no index inside the document. Both are recoverable without a
    /// data migration when a query needs them: <c>nvarchar(max)</c> is exactly what
    /// <c>ISJSON</c>, <c>JSON_VALUE</c> and a computed-column index all operate on, and SQL Server
    /// 2025's <c>json</c> type converts from it in place.</para>
    ///
    /// <para><b>nvarchar, not varchar.</b> These documents hold member-supplied text — a gym's name, a
    /// user's name, an audited field's old value — and the platform is Cyprus-based, so Greek
    /// characters are the ordinary case rather than the exotic one. A single-byte column would replace
    /// every one of them with <c>?</c> at write time, silently and irreversibly.</para>
    /// </summary>
    public const string JsonColumnType = "nvarchar(max)";
}
