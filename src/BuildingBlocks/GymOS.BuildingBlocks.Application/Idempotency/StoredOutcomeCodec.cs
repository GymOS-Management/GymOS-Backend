using System.Reflection;
using System.Text.Json;
using GymOS.SharedKernel.Results;

namespace GymOS.BuildingBlocks.Application.Idempotency;

/// <summary>
/// Serializes a handler outcome for the record table and reconstructs it for replay.
/// ResponseStatus 0 = success (body = the DTO, or null for plain Result); otherwise
/// ResponseStatus = (int)ErrorKind and body = {code, message}. The wire response stays
/// byte-identical because a replay re-enters the host's deterministic WriteResults mapping.
/// </summary>
public static class StoredOutcomeCodec
{
    private static readonly JsonSerializerOptions Options = JsonSerializerOptions.Default;

    private sealed record StoredError(string Code, string Message);

    /// <summary>Builds a live (non-replayed) failure of the pipeline's response type.</summary>
    public static TResponse Failure<TResponse>(Error error)
        where TResponse : Result
    {
        ArgumentNullException.ThrowIfNull(error);

        return typeof(TResponse) == typeof(Result)
            ? (TResponse)Result.Failure(error)
            : (TResponse)FailureFactory(typeof(TResponse).GetGenericArguments()[0]).Invoke(null, [error])!;
    }

    /// <summary>
    /// Encodes a handler outcome for storage: a success carries its value (or nothing, for a
    /// plain <see cref="Result"/>); a failure carries its <see cref="Error"/>'s code and message,
    /// tagged with its <see cref="ErrorKind"/> so <see cref="Decode{TResponse}"/> can reconstruct it.
    /// </summary>
    /// <param name="result">The outcome to encode. Must not be null.</param>
    /// <returns>
    /// The response status to persist (0 = success; otherwise <c>(int)ErrorKind + 1</c>) and the
    /// JSON body to persist alongside it (null for a valueless success).
    /// </returns>
    public static (int ResponseStatus, string? Body) Encode(Result result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsFailure)
        {
            Error error = result.Error!;
            return ((int)error.Kind + 1, JsonSerializer.Serialize(new StoredError(error.Code, error.Message), Options));
        }

        object? value = ValueOf(result);
        return (0, value is null ? null : JsonSerializer.Serialize(value, value.GetType(), Options));
    }

    /// <summary>
    /// Reconstructs a stored outcome as the caller's response type, the inverse of
    /// <see cref="Encode"/>. Used to replay a completed or deterministically failed record
    /// without re-executing the handler.
    /// </summary>
    /// <typeparam name="TResponse">The pipeline's declared response type (<c>Result</c> or <c>Result&lt;T&gt;</c>).</typeparam>
    /// <param name="responseStatus">The status previously produced by <see cref="Encode"/>.</param>
    /// <param name="body">The body previously produced by <see cref="Encode"/>.</param>
    /// <returns>The reconstructed <typeparamref name="TResponse"/>.</returns>
    public static TResponse Decode<TResponse>(int responseStatus, string? body)
        where TResponse : Result
    {
        if (responseStatus is 0)
        {
            if (typeof(TResponse) == typeof(Result))
            {
                return (TResponse)Result.Success();
            }

            Type valueType = typeof(TResponse).GetGenericArguments()[0];
            object value = JsonSerializer.Deserialize(body!, valueType, Options)!;
            return (TResponse)SuccessFactory(valueType).Invoke(null, [value])!;
        }

        StoredError stored = JsonSerializer.Deserialize<StoredError>(body!, Options)!;
        Error error = ErrorFor((ErrorKind)(responseStatus - 1), stored.Code, stored.Message);

        if (typeof(TResponse) == typeof(Result))
        {
            return (TResponse)Result.Failure(error);
        }

        return (TResponse)FailureFactory(typeof(TResponse).GetGenericArguments()[0]).Invoke(null, [error])!;
    }

    private static object? ValueOf(Result result)
    {
        Type type = result.GetType();
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Result<>)
            ? type.GetProperty(nameof(Result<object>.Value))!.GetValue(result)
            : null;
    }

    private static MethodInfo SuccessFactory(Type valueType) =>
        typeof(Result).GetMethods()
            .Single(m => m.Name == nameof(Result.Success) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(valueType);

    private static MethodInfo FailureFactory(Type valueType) =>
        typeof(Result).GetMethods()
            .Single(m => m.Name == nameof(Result.Failure) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(valueType);

    private static Error ErrorFor(ErrorKind kind, string code, string message) => kind switch
    {
        ErrorKind.Validation => Error.Validation(code, message),
        ErrorKind.NotFound => Error.NotFound(code, message),
        ErrorKind.Conflict => Error.Conflict(code, message),
        ErrorKind.Unauthorized => Error.Unauthorized(code, message),
        ErrorKind.Forbidden => Error.Forbidden(code, message),
        _ => Error.Failure(code, message),
    };
}
