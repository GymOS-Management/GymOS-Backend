using Microsoft.Extensions.Logging;

namespace GymOS.Platform.Application.Features;

/// <summary>
/// The one line every synchronous caller of <c>IFeatureExceptionMaintainer.WithdrawRedundantAsync</c>
/// writes when it withdrew something. Four call sites discarded the count entirely (M5 review); a
/// withdrawal is an operator-visible change to what a gym can do, and it should be findable in the log
/// by gym id and by what caused it.
/// </summary>
internal static partial class FeatureExceptionLogging
{
    [LoggerMessage(Level = LogLevel.Information, Message =
        "Withdrew {Count} feature override(s) for gym {GymId} made redundant by {Cause}.")]
    public static partial void LogOverridesWithdrawn(ILogger logger, int count, Guid gymId, string cause);
}
