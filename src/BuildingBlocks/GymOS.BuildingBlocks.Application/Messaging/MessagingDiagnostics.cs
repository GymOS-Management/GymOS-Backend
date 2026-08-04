using System.Diagnostics;

namespace GymOS.BuildingBlocks.Application.Messaging;

/// <summary>
/// The <c>gymos.commands</c> activity source (AD-38): one span per dispatched
/// command/query, tagged with message kind and type. The host registers the source
/// with OpenTelemetry; without a listener the cost is a null check.
/// </summary>
public static class MessagingDiagnostics
{
    public const string ActivitySourceName = "gymos.commands";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
