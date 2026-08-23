using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Invoices;
using GymOS.Platform.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The single-currency CHECK (subtotal/tax/total agree) is raw SQL in the AddInvoices
        // migration, not declared here: it spans three Money complex properties, and EF cannot
        // express a constraint across them. Same precedent as plan_prices' unique index.
        builder.ToTable("invoices", "platform");
        builder.HasKey(invoice => invoice.Id);

        builder.Property(invoice => invoice.Status).HasConversion<short>();

        // YYYY-NNNNNN is 11 characters; 20 leaves room for a numbering scheme change without a
        // migration, while still refusing anything that is obviously not an invoice number.
        builder.Property(invoice => invoice.InvoiceNumber).HasMaxLength(20);

        // The number is how the outside world names an invoice, so uniqueness is a property the
        // database holds, not a hope the sequence justifies.
        builder.HasIndex(invoice => invoice.InvoiceNumber)
            .IsUnique()
            .HasDatabaseName("ux_invoices_invoice_number");

        builder.Property(invoice => invoice.PaymentReference).HasMaxLength(200);

        builder.HasMoney(invoice => invoice.Subtotal, "subtotal");
        builder.HasMoney(invoice => invoice.TaxAmount, "tax");
        builder.HasMoney(invoice => invoice.Total, "total");

        // A fraction, not an amount (rule R4 exempts it by name): decimal(5,4) holds 0.0000–9.9999,
        // which is 0%–999.99% — any real rate fits with room for the absurd, and nothing rounds.
        builder.Property(invoice => invoice.TaxRate).HasColumnType("decimal(5,4)");

        // Restrict, not Cascade, on both parents: an invoice is the billing record. A gym is
        // soft-deleted and its subscriptions are kept for the same reason; a purge that ever
        // hard-deletes either must decide about the invoices explicitly rather than lose them.
        builder.HasOne<Gym>()
            .WithMany()
            .HasForeignKey(invoice => invoice.GymId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<GymSubscription>()
            .WithMany()
            .HasForeignKey(invoice => invoice.SubscriptionId)
            .OnDelete(DeleteBehavior.Restrict);

        // Cascade is right here, unlike the parents above: lines are part of the aggregate, and a
        // line without its invoice is not a record of anything.
        builder.HasMany(invoice => invoice.Lines)
            .WithOne()
            .HasForeignKey(line => line.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(invoice => invoice.Lines)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // The gym tab reads by gym in issue order; the subscription's history by subscription; the
        // M3.4 overdue sweep by status and due date.
        builder.HasIndex(invoice => new { invoice.GymId, invoice.CreatedAtUtc });
        builder.HasIndex(invoice => invoice.SubscriptionId);
        builder.HasIndex(invoice => new { invoice.Status, invoice.DueAtUtc });

        builder.HasGymOSRowVersion();
    }
}

public sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("invoice_lines", "platform");
        builder.HasKey(line => line.Id);

        // The aggregate refuses anything longer before it gets here — the constant keeps the two
        // ceilings the same number.
        builder.Property(line => line.Description).HasMaxLength(InvoiceLine.MaximumDescriptionLength);

        builder.HasMoney(line => line.UnitPrice, "unit_price");
        builder.HasMoney(line => line.Amount, "amount");

        builder.HasIndex(line => line.InvoiceId);
    }
}
