using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.SharedKernel.Currencies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace GymOS.Platform.Infrastructure.UnitTests;

/// <summary>
/// That <see cref="Money"/> can actually be persisted, asserted against the EF model.
///
/// <para>This runs before any entity holds a <see cref="Money"/> — which is the entire reason
/// <c>Money</c> was built ahead of the billing modules (AD-42). If the mapping shape does not work, the
/// time to find out is now, while no table exists and no migration has to be rewritten. The context
/// below is a throwaway that exists only to be mapped.</para>
/// </summary>
public sealed class MoneyMappingTests
{
    [Fact]
    public void AMoneyPropertyMapsToAnAmountColumnAndACurrencyColumn()
    {
        IEntityType entity = Model().FindEntityType(typeof(PricedThing))!;
        IComplexProperty price = entity.FindComplexProperty(nameof(PricedThing.Price))!;

        IProperty amount = price.ComplexType.FindProperty(nameof(Money.Amount))!;
        IProperty currency = price.ComplexType.FindProperty(nameof(Money.Currency))!;

        Assert.Equal("price_amount", amount.GetColumnName());
        Assert.Equal("price_currency", currency.GetColumnName());
    }

    [Fact]
    public void TheAmountColumnCarriesFourDecimalPlacesOfHeadroom()
    {
        IProperty amount = AmountProperty();

        // numeric(14,4): exchange results and per-unit prices carry more precision than settlement
        // does, and a column that truncated them would silently change the figure.
        Assert.Equal("numeric(14,4)", amount.GetColumnType());
        Assert.Equal(typeof(decimal), amount.ClrType);
    }

    [Fact]
    public void TheCurrencyColumnStoresOnlyTheCode()
    {
        IProperty currency = CurrencyProperty();

        // The minor-unit count is derived from the code. Storing it too would let a stale row claim
        // EUR has three decimal places.
        Assert.Equal(typeof(string), currency.GetValueConverter()!.ProviderClrType);
        Assert.Equal(3, currency.GetMaxLength());

        // One column, not a nested pair: the Currency struct is a scalar to the database.
        Assert.Null(currency.DeclaringType.FindComplexProperty(nameof(Money.Currency)));
    }

    [Fact]
    public void TheCurrencyConverterRoundTripsThroughTheCode()
    {
        var converter = MoneyConventions.CurrencyConverter;

        object? stored = converter.ConvertToProvider(Currency.Of("KWD"));
        object? restored = converter.ConvertFromProvider(stored);

        Assert.Equal("KWD", stored);

        // And it comes back knowing its minor units, which is the fact the code alone does not carry.
        Currency currency = Assert.IsType<Currency>(restored);
        Assert.Equal(3, currency.MinorUnits);
    }

    [Fact]
    public void TwoMoneyPropertiesOnOneEntityDoNotCollide()
    {
        IEntityType entity = Model().FindEntityType(typeof(PricedThing))!;

        string[] columns =
        [
            entity.FindComplexProperty(nameof(PricedThing.Price))!
                .ComplexType.FindProperty(nameof(Money.Amount))!.GetColumnName(),
            entity.FindComplexProperty(nameof(PricedThing.Tax))!
                .ComplexType.FindProperty(nameof(Money.Amount))!.GetColumnName(),
        ];

        // The prefix is passed in rather than derived, so an invoice with a net, a tax, and a gross
        // amount gets three unambiguous pairs.
        Assert.Equal(["price_amount", "tax_amount"], columns);
    }

    private static IProperty AmountProperty() =>
        Model().FindEntityType(typeof(PricedThing))!
            .FindComplexProperty(nameof(PricedThing.Price))!
            .ComplexType.FindProperty(nameof(Money.Amount))!;

    private static IProperty CurrencyProperty() =>
        Model().FindEntityType(typeof(PricedThing))!
            .FindComplexProperty(nameof(PricedThing.Price))!
            .ComplexType.FindProperty(nameof(Money.Currency))!;

    private static IModel Model()
    {
        using var context = new MoneyProbeContext();
        return context.Model;
    }

    /// <summary>An entity that exists only so the mapping has something to map.</summary>
    private sealed class PricedThing
    {
        public Guid Id { get; set; }

        public Money Price { get; set; }

        public Money Tax { get; set; }
    }

    private sealed class MoneyProbeContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            // A connection string is never opened: EF resolves the model from configuration alone, so
            // this runs in milliseconds and gates every build rather than only the ones with Docker.
            optionsBuilder
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PricedThing>(thing =>
            {
                thing.ToTable("priced_things");
                thing.HasKey(entity => entity.Id);
                thing.HasMoney(entity => entity.Price, "price");
                thing.HasMoney(entity => entity.Tax, "tax");
            });
        }
    }
}
