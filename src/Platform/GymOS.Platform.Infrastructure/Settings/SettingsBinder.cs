using GymOS.Platform.Application.Settings.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GymOS.Platform.Infrastructure.Settings;

/// <summary>Where a bound <typeparamref name="T"/> reads its section from — one registration per T.</summary>
public sealed record SettingsBinding<T>(string SectionName) where T : class, new();

/// <summary>
/// <see cref="ISettings{T}"/> over the merged layers (M7 spec §4): the real configuration with the
/// provider's database overlay on top, bound into the existing options class and run through its
/// existing validators. An invalid row never takes a valid platform down: the last valid value, else
/// the layers below it.
/// Singleton, process-wide — the last-valid fallback and the bound overlay must survive past the
/// request that observed a bad row, or the next fresh scope would throw on the very state this class
/// exists to hide. Its dependencies are all singleton-safe: <see cref="ISettingsProvider"/> is a
/// process-wide cache, <see cref="IConfiguration"/> and <see cref="SettingsBinding{T}"/> are immutable,
/// and every <see cref="IValidateOptions{T}"/> registered through <c>AddOptions&lt;T&gt;()</c>'s
/// <c>ValidateDataAnnotations</c>/<c>Validate</c> is itself singleton.
/// When the overlay fails validation and there is no previous valid value at all, the configuration-only
/// fallback (below-overlay bind) is itself cached as <c>_lastValid</c> against the failing overlay's
/// reference, so a persistently invalid overlay binds the fallback once, not on every read — a later,
/// different overlay reference still forces a fresh bind and can supersede it.
/// </summary>
internal sealed partial class SettingsBinder<T> : ISettings<T> where T : class, new()
{
    private static readonly IReadOnlyDictionary<string, string?> NoOverlay = new Dictionary<string, string?>();

    private readonly ISettingsProvider _provider;
    private readonly IConfiguration _configuration;
    private readonly SettingsBinding<T> _binding;
    private readonly IEnumerable<IValidateOptions<T>> _validators;
    private readonly ILogger<SettingsBinder<T>> _logger;
    private readonly Lock _lock = new();
    private T? _lastValid;
    private IReadOnlyDictionary<string, string?>? _boundOverlay;

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Settings for {Options} failed validation and the previous values remain in effect: {Failures}")]
    private static partial void LogInvalid(ILogger logger, string options, string failures);

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Settings for {Options} failed validation and there is no previous valid value to serve; " +
        "falling back to configuration and defaults — the state already proved at start-up: {Failures}")]
    private static partial void LogFellBackToConfiguration(ILogger logger, string options, string failures);

    public SettingsBinder(
        ISettingsProvider provider, IConfiguration configuration, SettingsBinding<T> binding,
        IEnumerable<IValidateOptions<T>> validators, ILogger<SettingsBinder<T>> logger)
    {
        _provider = provider; _configuration = configuration; _binding = binding; _validators = validators; _logger = logger;
    }

    public T Current
    {
        get
        {
            // Synchronous and cheap: the provider serves its last loaded overlay from memory and only
            // occasionally kicks off a background refresh (stale-while-revalidate) — never a blocking
            // database round trip except on a cold process.
            IReadOnlyDictionary<string, string?> overlay = _provider.GetOverlayNow();

            // Locked end to end: two concurrent first reads must bind exactly once, and every field
            // this getter touches is shared process-wide now that the binder is a singleton.
            lock (_lock)
            {
                if (_lastValid is not null && ReferenceEquals(overlay, _boundOverlay))
                {
                    return _lastValid;
                }

                (T? bound, string? failures) = Bind(overlay);
                _boundOverlay = overlay;
                if (bound is not null)
                {
                    _lastValid = bound;
                    return bound;
                }

                if (_lastValid is { } previous)
                {
                    LogInvalid(_logger, typeof(T).Name, failures ?? "unknown");
                    return previous;
                }

                // No previous valid value exists at all — this is either the very first read or every
                // read so far has been invalid. Fall back to the layer below the overlay: configuration
                // plus code defaults, exactly the state `ValidateOnStart` already proved at boot.
                (T? belowOverlay, string? fallbackFailures) = Bind(NoOverlay);
                LogFellBackToConfiguration(_logger, typeof(T).Name, failures ?? "unknown");
                if (belowOverlay is not null)
                {
                    // Cache the fallback itself: _boundOverlay above still points at the invalid overlay
                    // that got us here, so a repeat read with that same (reference-equal) overlay is now
                    // a cache hit instead of rebinding twice. A later, different overlay reference still
                    // forces a fresh bind and can supersede this fallback.
                    _lastValid = belowOverlay;
                    return belowOverlay;
                }

                throw new OptionsValidationException(
                    _binding.SectionName, typeof(T), [fallbackFailures ?? failures ?? "invalid"]);
            }
        }
    }

    /// <summary>Binds and validates a proposed overlay without touching the cache — the write path's check.</summary>
    public (T? Value, string? Failures) Bind(IReadOnlyDictionary<string, string?> overlay)
    {
        IConfigurationRoot merged = new ConfigurationBuilder()
            .AddConfiguration(_configuration)
            .AddInMemoryCollection(overlay) // later wins: rows over configuration
            .Build();

        // ConfigurationRoot registers change-token callbacks against every provider it holds, including
        // _configuration (the host's own root, via AddConfiguration); only Dispose() releases them, and
        // this instance is never referenced again once we've read out of it. AddConfiguration(config)
        // does not itself dispose the inner configuration, so disposing `merged` here is safe and does
        // not tear down the host's configuration.
        using IDisposable _ = (IDisposable)merged;

        var instance = new T();
        merged.GetSection(_binding.SectionName).Bind(instance);

        List<string> failures = [];
        foreach (IValidateOptions<T> validator in _validators)
        {
            ValidateOptionsResult result = validator.Validate(Options.DefaultName, instance);
            if (result.Failed)
            {
                failures.AddRange(result.Failures ?? [result.FailureMessage ?? "invalid"]);
            }
        }

        return failures.Count == 0 ? (instance, null) : (null, string.Join("; ", failures));
    }
}
