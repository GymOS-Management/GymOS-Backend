using GymOS.Platform.Domain.Invoices;
using GymOS.Platform.Domain.Invoices.Events;
using GymOS.Platform.Domain.Invoices.Rules;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Rules;
using GymOS.TestingSupport;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

public sealed class InvoiceTests
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid GymId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SubscriptionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Currency Eur = Currency.Euro;
    private const string Number = "2026-000001";

    private static InvoiceLineSpec Line(
        decimal amount,
        string description = "Pro — 2026-08-09 to 2026-09-09",
        Currency? currency = null) =>
        new(description, 1, Money.Of(amount, currency ?? Eur));

    private static Invoice Issue(decimal taxRate, params InvoiceLineSpec[] lines) =>
        Invoice.Issue(
            GymId,
            SubscriptionId,
            Number,
            lines,
            taxRate,
            IssuedAt,
            IssuedAt.AddDays(InvoicePaymentTerms.Days));

    private static Invoice Issued(decimal amount = 49.99m)
    {
        Invoice invoice = Issue(0m, Line(amount));
        invoice.DequeueDomainEvents();
        return invoice;
    }

    // ── Issuing ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Issue_StartsIssuedAndRecordsWhatWasAgreed()
    {
        Invoice invoice = Issue(0m, Line(49.99m));

        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
        Assert.Equal(GymId, invoice.GymId);
        Assert.Equal(SubscriptionId, invoice.SubscriptionId);
        Assert.Equal(Number, invoice.InvoiceNumber);
        Assert.Equal(IssuedAt, invoice.IssuedAtUtc);
        Assert.Equal(IssuedAt.AddDays(14), invoice.DueAtUtc);
        Assert.Null(invoice.PaidAtUtc);
        Assert.Null(invoice.PaymentReference);

        var issued = Assert.IsType<InvoiceIssued>(Assert.Single(invoice.DomainEvents));
        Assert.Equal(invoice.Id, issued.InvoiceId);
        Assert.Equal(Number, issued.InvoiceNumber);
        Assert.Equal(49.99m, issued.Total);
        Assert.Equal("EUR", issued.Currency);
        Assert.Equal(invoice.DueAtUtc, issued.DueAtUtc);
    }

    [Fact]
    public void Issue_WithoutTax_TotalsTheLines()
    {
        Invoice invoice = Issue(0m, Line(30m), Line(19.99m, "Setup"));

        Assert.Equal(Money.Of(49.99m, Eur), invoice.Subtotal);
        Assert.Equal(Money.Zero(Eur), invoice.TaxAmount);
        Assert.Equal(Money.Of(49.99m, Eur), invoice.Total);
        Assert.Equal(0m, invoice.TaxRate);
    }

    [Fact]
    public void Issue_WithTax_AddsItToTheSubtotal()
    {
        Invoice invoice = Issue(0.19m, Line(100m));

        Assert.Equal(Money.Of(100m, Eur), invoice.Subtotal);
        Assert.Equal(Money.Of(19m, Eur), invoice.TaxAmount);
        Assert.Equal(Money.Of(119m, Eur), invoice.Total);
        Assert.Equal(0.19m, invoice.TaxRate);
    }

    [Fact]
    public void Issue_RoundsTaxToTheCurrencysMinorUnits()
    {
        // Not to two decimals: KWD divides into thousandths, and rounding its tax to cents would
        // charge the wrong amount every time. Money.Multiply rounds to whatever the currency says.
        Currency kwd = Currency.Of("KWD");
        Invoice invoice = Issue(0.1235m, Line(10m, currency: kwd));

        Assert.Equal(Money.Of(1.235m, kwd), invoice.TaxAmount);
        Assert.Equal(Money.Of(11.235m, kwd), invoice.Total);
    }

    [Fact]
    public void Issue_RoundsTaxHalfToEvenOnACurrencyWithNoMinorUnit()
    {
        // ¥1005 × 10% is ¥100.5, and yen have no fractions. Banker's rounding is the house policy for
        // a series of amounts — it does not drift upward the way half-up does.
        Currency jpy = Currency.Of("JPY");
        Invoice invoice = Issue(0.1m, Line(1005m, currency: jpy));

        Assert.Equal(Money.Of(100m, jpy), invoice.TaxAmount);
        Assert.Equal(Money.Of(1105m, jpy), invoice.Total);
    }

    [Fact]
    public void Issue_CopiesTheUnitPriceOntoTheLineAmount()
    {
        Invoice invoice = Issue(0m, Line(49.99m, "Pro — August"));

        InvoiceLine line = Assert.Single(invoice.Lines);
        Assert.Equal("Pro — August", line.Description);
        Assert.Equal(1, line.Quantity);
        Assert.Equal(Money.Of(49.99m, Eur), line.UnitPrice);
        Assert.Equal(Money.Of(49.99m, Eur), line.Amount);
        Assert.Equal(invoice.Id, line.InvoiceId);
    }

    [Fact]
    public void Issue_NetsACreditLineAgainstACharge()
    {
        // A prorated upgrade: unused time on the old plan credited, the new plan's remainder charged.
        Invoice invoice = Issue(0m, Line(-16.12m, "Credit — unused subscription time"), Line(41.29m));

        Assert.Equal(Money.Of(25.17m, Eur), invoice.Subtotal);
        Assert.Equal(Money.Of(25.17m, Eur), invoice.Total);
        Assert.Equal(2, invoice.Lines.Count);
    }

    [Fact]
    public void Issue_WithNoLines_Throws() =>
        Assert.Throws<DomainException>(() => Issue(0m));

    [Fact]
    public void Issue_WithMixedCurrencies_Throws()
    {
        // Refused up front with an invoice-level message rather than left to Money's mismatch, so the
        // error names the actual mistake: an invoice is settled in one currency.
        Assert.Throws<DomainException>(
            () => Issue(0m, Line(30m), Line(19.99m, currency: Currency.Of("USD"))));
    }

    [Fact]
    public void Issue_WithAQuantityOtherThanOne_Throws()
    {
        // M3.3 issues one line per thing charged, so Amount = UnitPrice is exact. A future producer of
        // quantity > 1 has to add the multiply-with-a-rounding-policy path deliberately instead of
        // inheriting a silent under-charge.
        var spec = new InvoiceLineSpec("Ten sessions", 10, Money.Of(10m, Eur));

        Assert.Throws<DomainException>(
            () => Invoice.Issue(
                GymId, SubscriptionId, Number, [spec], 0m, IssuedAt, IssuedAt.AddDays(14)));
    }

    [Fact]
    public void Issue_WithANegativeTotal_Throws()
    {
        // A net credit is scheduled as a plan change at period end, never issued as a negative
        // invoice — so a negative total reaching here is a caller defect.
        Assert.Throws<DomainException>(() => Issue(0m, Line(-10m, "Credit")));
    }

    [Fact]
    public void Issue_WithoutAGym_Throws() =>
        Assert.Throws<DomainException>(
            () => Invoice.Issue(
                Guid.Empty, SubscriptionId, Number, [Line(10m)], 0m, IssuedAt, IssuedAt.AddDays(14)));

    [Fact]
    public void Issue_WithoutASubscription_Throws() =>
        Assert.Throws<DomainException>(
            () => Invoice.Issue(
                GymId, Guid.Empty, Number, [Line(10m)], 0m, IssuedAt, IssuedAt.AddDays(14)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Issue_WithoutANumber_Throws(string? number) =>
        Assert.Throws<DomainException>(
            () => Invoice.Issue(
                GymId, SubscriptionId, number!, [Line(10m)], 0m, IssuedAt, IssuedAt.AddDays(14)));

    [Fact]
    public void Issue_WithADueDateBeforeItsIssueDate_Throws() =>
        Assert.Throws<DomainException>(
            () => Invoice.Issue(
                GymId, SubscriptionId, Number, [Line(10m)], 0m, IssuedAt, IssuedAt.AddDays(-1)));

    [Fact]
    public void Issue_WithANegativeTaxRate_Throws() =>
        Assert.Throws<DomainException>(() => Issue(-0.19m, Line(100m)));

    // ── Payment ──────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkPaid_RecordsTheReferenceAndRaisesTheEvent()
    {
        Invoice invoice = Issued();
        DateTimeOffset paidAt = IssuedAt.AddDays(3);

        invoice.MarkPaid("  stripe_pi_3Nz  ", paidAt);

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(paidAt, invoice.PaidAtUtc);
        Assert.Equal("stripe_pi_3Nz", invoice.PaymentReference);

        var paid = Assert.IsType<InvoicePaid>(Assert.Single(invoice.DomainEvents));
        Assert.Equal(invoice.Id, paid.InvoiceId);
        Assert.Equal(GymId, paid.GymId);
        Assert.Equal(Number, paid.InvoiceNumber);
        Assert.Equal("stripe_pi_3Nz", paid.PaymentReference);
        Assert.Equal(49.99m, paid.Total);
        Assert.Equal("EUR", paid.Currency);
    }

    [Fact]
    public void MarkPaid_Twice_Throws()
    {
        Invoice invoice = Issued();
        invoice.MarkPaid("stripe_pi_3Nz", IssuedAt);

        // The domain refusal is the backstop; the handler checks the status first so the caller gets a
        // precise code, and AD-39 stops a retried request from ever reaching either.
        Assert.Throws<DomainException>(() => invoice.MarkPaid("stripe_pi_3Nz", IssuedAt));
    }

    [Fact]
    public void MarkPaid_AfterVoiding_Throws()
    {
        Invoice invoice = Issued();
        invoice.MarkVoid();

        Assert.Throws<DomainException>(() => invoice.MarkPaid("stripe_pi_3Nz", IssuedAt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MarkPaid_WithoutAReference_IsRefused(string? reference)
    {
        Invoice invoice = Issued();

        var violation = Assert.Throws<BusinessRuleViolationException>(
            () => invoice.MarkPaid(reference!, IssuedAt));

        Assert.Equal("invoices.payment_reference_invalid", violation.Code);
        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
    }

    [Fact]
    public void MarkPaid_WithAnOverlongReference_IsRefused()
    {
        Invoice invoice = Issued();

        var violation = Assert.Throws<BusinessRuleViolationException>(
            () => invoice.MarkPaid(new string('x', 201), IssuedAt));

        Assert.Equal("invoices.payment_reference_invalid", violation.Code);
    }

    // ── Voiding ──────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkVoid_CancelsTheInvoiceAndRaisesTheEvent()
    {
        Invoice invoice = Issued();

        invoice.MarkVoid();

        Assert.Equal(InvoiceStatus.Void, invoice.Status);

        var voided = Assert.IsType<InvoiceVoided>(Assert.Single(invoice.DomainEvents));
        Assert.Equal(invoice.Id, voided.InvoiceId);
        Assert.Equal(Number, voided.InvoiceNumber);
        Assert.Equal(49.99m, voided.Total);
        Assert.Equal("EUR", voided.Currency);
    }

    [Fact]
    public void MarkVoid_AfterPayment_Throws()
    {
        Invoice invoice = Issued();
        invoice.MarkPaid("stripe_pi_3Nz", IssuedAt);

        // Money has moved. Reversing it is a credit note, which needs its own decision (deferred).
        Assert.Throws<DomainException>(invoice.MarkVoid);
    }

    [Fact]
    public void MarkVoid_Twice_Throws()
    {
        Invoice invoice = Issued();
        invoice.MarkVoid();

        Assert.Throws<DomainException>(invoice.MarkVoid);
    }

    // ── Overdue is derived, never stored ─────────────────────────────────────────

    [Fact]
    public void IsOverdue_OnlyOnceAnIssuedInvoicePassesItsDueDate()
    {
        Invoice invoice = Issued();

        Assert.False(invoice.IsOverdue(invoice.DueAtUtc.AddSeconds(-1)));
        Assert.True(invoice.IsOverdue(invoice.DueAtUtc));
        Assert.True(invoice.IsOverdue(invoice.DueAtUtc.AddDays(1)));
    }

    [Fact]
    public void IsOverdue_IsFalseOnceSettled()
    {
        // A stored flag would need a job to keep it true, and the night that job failed it would lie —
        // the M3.2 trial-expiry precedent. Derived here, and derived again in the read models.
        Invoice paid = Issued();
        paid.MarkPaid("stripe_pi_3Nz", IssuedAt);

        Invoice voided = Issued();
        voided.MarkVoid();

        Assert.False(paid.IsOverdue(paid.DueAtUtc.AddDays(30)));
        Assert.False(voided.IsOverdue(voided.DueAtUtc.AddDays(30)));
    }

    // ── The rule on its own ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("stripe_pi_3Nz")]
    [InlineData("Bank transfer 2026-08-09")]
    [InlineData("x")]
    public void AUsablePaymentReferenceIsAccepted(string reference) =>
        BusinessRuleAssert.Satisfied(new PaymentReferenceMustBePresent(reference));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyPaymentReferenceIsRejected(string? reference) =>
        BusinessRuleAssert.Broken(
            new PaymentReferenceMustBePresent(reference), "invoices.payment_reference_invalid");

    [Fact]
    public void APaymentReferenceLongerThanTheColumnIsRejected() =>
        BusinessRuleAssert.Broken(
            new PaymentReferenceMustBePresent(new string('x', 201)),
            "invoices.payment_reference_invalid");

    [Fact]
    public void APaymentReferenceIsMeasuredAfterTrimming() =>
        BusinessRuleAssert.Satisfied(
            new PaymentReferenceMustBePresent("  " + new string('x', 200) + "  "));

    [Fact]
    public void InvoiceRuleCodesFollowTheConvention() =>
        BusinessRuleAssert.CodesFollowConvention(new PaymentReferenceMustBePresent("ref"));
}
