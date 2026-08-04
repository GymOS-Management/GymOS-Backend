using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.SharedKernel.UnitTests;

public sealed class ResultTests
{
    [Fact]
    public void Success_HasNoError()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_ExposesItsError()
    {
        var error = Error.Conflict("gyms.slug_taken", "The slug is already in use.");
        var result = Result.Failure(error);

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
        Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
    }

    [Fact]
    public void GenericSuccess_ExposesItsValue()
    {
        var result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void ReadingValue_OfFailedResult_Throws()
    {
        var result = Result.Failure<int>(Error.NotFound("gyms.not_found", "No such gym."));

        var exception = Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Contains("gyms.not_found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Match_InvokesTheCorrectBranch()
    {
        var success = Result.Success("value");
        var failure = Result.Failure<string>(Error.Validation("input.bad", "Bad input."));

        Assert.Equal("ok:value", success.Match(v => "ok:" + v, e => "err:" + e.Code));
        Assert.Equal("err:input.bad", failure.Match(v => "ok:" + v, e => "err:" + e.Code));
    }

    [Fact]
    public void ErrorFactories_ProduceTheirKinds()
    {
        Assert.Equal(ErrorKind.Validation, Error.Validation("c", "m").Kind);
        Assert.Equal(ErrorKind.NotFound, Error.NotFound("c", "m").Kind);
        Assert.Equal(ErrorKind.Conflict, Error.Conflict("c", "m").Kind);
        Assert.Equal(ErrorKind.Unauthorized, Error.Unauthorized("c", "m").Kind);
        Assert.Equal(ErrorKind.Forbidden, Error.Forbidden("c", "m").Kind);
        Assert.Equal(ErrorKind.Failure, Error.Failure("c", "m").Kind);
    }
}
