namespace GymOS.SharedKernel.Results;

/// <summary>
/// Outcome of a business operation that yields a value on success.
/// Construct via <see cref="Result.Success{TValue}(TValue)"/> and
/// <see cref="Result.Failure{TValue}(Error)"/>.
/// </summary>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue value)
        : base(true, null)
        => _value = value;

    internal Result(Error error)
        : base(false, error)
    {
    }

    /// <summary>
    /// The success value. Accessing it on a failed result is a programming defect
    /// and throws — check <see cref="Result.IsSuccess"/> or use <see cref="Match"/>.
    /// </summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            $"Cannot read {nameof(Value)} of a failed result ({Error!.Code}). " +
            $"Check {nameof(IsSuccess)} or use {nameof(Match)}.");

    public TOut Match<TOut>(Func<TValue, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(_value!) : onFailure(Error!);
    }
}
