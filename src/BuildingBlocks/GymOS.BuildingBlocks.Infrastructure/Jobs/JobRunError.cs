namespace GymOS.BuildingBlocks.Infrastructure.Jobs;

/// <summary>
/// The one shape of error text a job run may store: the exception's type name and message, cut
/// to the column width. Never the stack trace and never the inner chain — a <c>SqlException</c>
/// message can name a server, and <c>job_runs.error</c> is read by a console panel with
/// <c>monitoring.read</c>, not by a debugger. The full exception is in the structured log under
/// the same job name and time.
/// </summary>
public static class JobRunError
{
    public const int MaxLength = 2000;

    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string text = $"{exception.GetType().Name}: {exception.Message}";

        return text.Length <= MaxLength ? text : text[..MaxLength];
    }
}
