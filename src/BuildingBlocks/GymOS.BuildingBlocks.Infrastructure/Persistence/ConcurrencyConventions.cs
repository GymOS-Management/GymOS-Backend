using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GymOS.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The optimistic concurrency token every mutable GymOS entity carries, declared in one place so
/// every table spells it the same way.
///
/// <para><b>Why this file exists at all.</b> Under PostgreSQL there was nothing to declare: each
/// configuration wrote <c>builder.Property&lt;uint&gt;("xmin").IsRowVersion()</c> and mapped onto
/// <c>xmin</c>, a system column PostgreSQL maintains on every table whether or not anyone asks. SQL
/// Server has no such column — <c>rowversion</c> is a real column that has to be added to each table
/// — so the declaration became load-bearing, and a table that spelled it differently would silently
/// have no concurrency control at all. <c>ConcurrencyModelTests</c> asserts every mutable entity has
/// one, which is what turns that from a convention into a rule.</para>
/// </summary>
public static class ConcurrencyConventions
{
    /// <summary>
    /// The shadow property's name. <c>row_version</c> after the snake-case naming convention, which
    /// is the column name migrations emit and raw SQL must use.
    /// </summary>
    public const string RowVersionProperty = "RowVersion";

    /// <summary>
    /// Adds the concurrency token: a <c>rowversion</c> column the server maintains, surfaced to the
    /// application as a <see cref="ulong"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Server-maintained, like <c>xmin</c> was, and for the same reason.</b> SQL Server
    /// advances a <c>rowversion</c> on every UPDATE to the row without anything in application code
    /// remembering to bump it. A hand-incremented integer column would have to be incremented
    /// correctly by every write path forever, and the one that forgot would be the one that silently
    /// stopped detecting conflicts.</para>
    /// <para><b><see cref="ulong"/> rather than <c>byte[]</c>, to keep the token numeric.</b>
    /// <c>rowversion</c> is eight bytes and EF's natural mapping is <c>byte[]</c>, which would have
    /// made the API's opaque version token a base64 string instead of the digits it has always been.
    /// The wire contract does not care — <c>rowVersion</c> is typed <c>string</c> in
    /// <c>contracts/openapi-v1.json</c> and callers are told it is opaque — but every call site that
    /// parses, compares or fakes a version would have changed shape, so the numeric form is kept and
    /// only its width moves (uint to ulong). <see cref="NumberToBytesConverter{T}"/> is EF's built-in
    /// converter for exactly this.</para>
    /// <para><b>The number is not ordered, and nothing may assume it is.</b>
    /// <c>NumberToBytesConverter</c> does not reverse <c>rowversion</c>'s big-endian bytes, so a later
    /// version does not reliably compare greater than an earlier one. That is fine and was already
    /// true: <c>xmin</c> is a transaction id that wraps around, and the token is only ever used for
    /// equality — "is this the row I read?" — never for ordering.</para>
    /// </remarks>
    public static EntityTypeBuilder<TEntity> HasGymOSRowVersion<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Property<ulong>(RowVersionProperty)
            .IsRowVersion()
            .HasConversion(new NumberToBytesConverter<ulong>())
            .HasColumnType("rowversion");

        return builder;
    }
}
