using GymOS.SharedKernel.Rules;

namespace GymOS.Platform.Domain.Invoices.Rules;

/// <summary>
/// How a payment was identified: a provider's reference, a bank transfer description, a receipt
/// number — free text, because the platform does not process payments itself and cannot constrain
/// what the person recording one has in front of them.
///
/// <para><b>Required, though.</b> "Paid" without a reference is an assertion nobody can check, and the
/// question it has to answer months later — which payment settled this invoice — has no other source.
/// The ceiling matches the column so the rule refuses what the database would refuse anyway, with a
/// message instead of a truncation error.</para>
/// </summary>
public sealed record PaymentReferenceMustBePresent(string? PaymentReference) : IBusinessRule
{
    public const int MaximumLength = 200;

    public string Code => "invoices.payment_reference_invalid";

    public string Message =>
        "Recording a payment needs a reference identifying it — a provider id, a bank transfer " +
        $"description, or a receipt number — of at most {MaximumLength} characters.";

    /// <summary>
    /// Measured after trimming, because the length the rule cares about is what gets stored, and the
    /// aggregate stores the trimmed value.
    /// </summary>
    public bool IsBroken() =>
        string.IsNullOrWhiteSpace(PaymentReference) ||
        PaymentReference.Trim().Length > MaximumLength;
}
