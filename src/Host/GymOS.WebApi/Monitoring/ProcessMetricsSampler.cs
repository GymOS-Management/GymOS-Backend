using System.Diagnostics;
using GymOS.Platform.Application.Monitoring.Abstractions;

namespace GymOS.WebApi.Monitoring;

/// <summary>
/// Samples this process every 15 seconds so <c>/monitoring/overview</c> returns the last reading and
/// never blocks on a measurement (M6 spec §4). CPU is the process-time delta over the interval divided
/// by the core count, so it is null on the first sample. One replica's view: <c>Instance</c> says which.
/// Lives in the host because <see cref="Process"/> is a host concern, like the health adapter.
/// </summary>
internal sealed class ProcessMetricsSampler : BackgroundService, IProcessMetrics
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time;
    private volatile ProcessMetrics? _current;

    public ProcessMetricsSampler(TimeProvider time) => _time = time;

    public ProcessMetrics? Current => _current;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using Process process = Process.GetCurrentProcess();
        TimeSpan lastCpu = process.TotalProcessorTime;
        DateTimeOffset lastAt = _time.GetUtcNow();
        Publish(process, cpuPercent: null, lastAt);

        using var timer = new PeriodicTimer(Interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                process.Refresh();
                TimeSpan cpu = process.TotalProcessorTime;
                DateTimeOffset at = _time.GetUtcNow();

                double elapsedMs = (at - lastAt).TotalMilliseconds;
                double? percent = elapsedMs <= 0
                    ? null
                    : Math.Round((cpu - lastCpu).TotalMilliseconds / elapsedMs / Environment.ProcessorCount * 100, 1);

                lastCpu = cpu;
                lastAt = at;
                Publish(process, percent, at);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    private void Publish(Process process, double? cpuPercent, DateTimeOffset sampledAt) =>
        _current = new ProcessMetrics(
            cpuPercent,
            process.WorkingSet64 / (1024 * 1024),
            (long)(sampledAt - process.StartTime.ToUniversalTime()).TotalSeconds,
            Environment.MachineName,
            sampledAt);
}
