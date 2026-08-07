using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Idempotency;

public sealed class StoredOutcomeCodecTests
{
    private sealed record PaymentDto(Guid Id, string Reference);

    [Fact]
    public void Success_with_value_round_trips()
    {
        var dto = new PaymentDto(Guid.NewGuid(), "PAY-1");
        (int status, string? body) = StoredOutcomeCodec.Encode(Result.Success(dto));

        var decoded = StoredOutcomeCodec.Decode<Result<PaymentDto>>(status, body);

        Assert.Equal(0, status);
        Assert.True(decoded.IsSuccess);
        Assert.Equal(dto, decoded.Value);
    }

    [Fact]
    public void Plain_success_round_trips()
    {
        (int status, string? body) = StoredOutcomeCodec.Encode(Result.Success());
        var decoded = StoredOutcomeCodec.Decode<Result>(status, body);
        Assert.True(decoded.IsSuccess);
    }

    [Fact]
    public void Business_failure_round_trips_with_code_message_and_kind()
    {
        Result<PaymentDto> failed = Result.Failure<PaymentDto>(
            Error.Conflict("invoices.already_paid", "This invoice is already paid."));
        (int status, string? body) = StoredOutcomeCodec.Encode(failed);

        var decoded = StoredOutcomeCodec.Decode<Result<PaymentDto>>(status, body);

        Assert.True(decoded.IsFailure);
        Assert.Equal("invoices.already_paid", decoded.Error!.Code);
        Assert.Equal("This invoice is already paid.", decoded.Error.Message);
        Assert.Equal(ErrorKind.Conflict, decoded.Error.Kind);
    }

    [Fact]
    public void Generic_success_with_a_null_value_is_rejected_at_encode_time()
    {
        Result<PaymentDto> nullSuccess = Result.Success<PaymentDto>(null!);

        var exception = Assert.Throws<InvalidOperationException>(() => StoredOutcomeCodec.Encode(nullSuccess));

        Assert.Contains("must be non-null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_that_no_longer_parses_names_the_OperationKey_versioning_rule()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => StoredOutcomeCodec.Decode<Result<PaymentDto>>(0, "{ not valid json"));

        Assert.Contains("requires a new OperationKey", exception.Message, StringComparison.Ordinal);
    }
}
