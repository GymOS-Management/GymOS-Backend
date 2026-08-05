using System.Linq.Expressions;
using GymOS.SharedKernel.Currencies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GymOS.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// How <see cref="Money"/> reaches PostgreSQL (ARCHITECTURE-ADDENDUM.md §3.1): an amount column and a
/// currency column, side by side, configured identically everywhere.
///
/// <para>Centralized because the pair only works if both halves agree. An amount stored without its
/// currency is a number nobody can interpret, and two tables that disagree on the amount's precision
/// produce figures that differ after a join.</para>
/// </summary>
public static class MoneyConventions
{
    /// <summary>
    /// <c>numeric(14,4)</c> — up to 10 digits before the decimal point and four after.
    ///
    /// <para>Four decimal places, not two, for the headroom the spec asks for: exchange results and
    /// per-unit prices carry intermediate precision, and a column that truncates them would silently
    /// change the number. Every currency ISO 4217 defines settles in four places or fewer
    /// (<see cref="Currency.MaximumMinorUnits"/>), so nothing is lost at settlement either.</para>
    /// </summary>
    public const string AmountColumnType = "numeric(14,4)";

    /// <summary>ISO 4217 alphabetic codes are exactly three characters.</summary>
    public const int CurrencyCodeLength = 3;

    /// <summary>
    /// Converts a <see cref="Currency"/> to its code and back.
    ///
    /// <para>Only the code is stored. The minor-unit count is derived from it, and persisting a derived
    /// value would create a second source of truth that a stale row could disagree with — a row
    /// claiming EUR has three decimal places would be worse than no row at all.</para>
    /// </summary>
    public static ValueConverter<Currency, string> CurrencyConverter { get; } =
        new(currency => currency.Code, code => Currency.Of(code));

    /// <summary>
    /// Maps a <see cref="Money"/>-typed property to its two columns:
    /// <c>{prefix}_amount</c> and <c>{prefix}_currency</c>.
    /// </summary>
    /// <param name="columnPrefix">
    /// Snake-case name the pair shares, e.g. <c>"total"</c> → <c>total_amount</c> +
    /// <c>total_currency</c>. Stated explicitly rather than derived from the property name so a rename
    /// in code is never an accidental migration.
    /// </param>
    public static ComplexPropertyBuilder<Money> HasMoney<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, Money>> propertyExpression,
        string columnPrefix)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnPrefix);

        ComplexPropertyBuilder<Money> money = builder.ComplexProperty(propertyExpression);

        money.Property(amount => amount.Amount)
            .HasColumnName($"{columnPrefix}_amount")
            .HasColumnType(AmountColumnType);

        money.Property(amount => amount.Currency)
            .HasColumnName($"{columnPrefix}_currency")
            .HasMaxLength(CurrencyCodeLength)
            .HasConversion(CurrencyConverter);

        return money;
    }
}
