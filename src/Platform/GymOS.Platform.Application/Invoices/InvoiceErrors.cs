using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Invoices;

/// <summary>Failure vocabulary of the invoice surface. Codes are stable API.</summary>
public static class InvoiceErrors
{
    public static Error NotFound(Guid invoiceId) => Error.NotFound(
        "invoices.not_found",
        $"Invoice '{invoiceId}' does not exist.");

    public static Error AlreadyPaid => Error.Conflict(
        "invoices.already_paid",
        "This invoice has already been paid. Recording another payment against it needs a human " +
        "to work out where the second payment belongs.");

    public static Error AlreadyVoid => Error.Conflict(
        "invoices.already_void",
        "This invoice has been voided; there is nothing left to do to it.");

    public static Error NotPayable => Error.Conflict(
        "invoices.not_payable",
        "Only an issued invoice can be paid.");

    public static Error ConfirmationMismatch => Error.Conflict(
        "invoices.confirmation_mismatch",
        "Voiding an invoice requires the X-Confirm-Action header carrying its invoice number.");
}
