using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Xyo.Sdk.Telemetry;

/// <summary>
/// Central registration point for the SDK's OpenTelemetry instrumentation.
/// </summary>
/// <remarks>
/// <para>
/// A single <see cref="ActivitySource"/> and a single <see cref="Meter"/>, both named <see cref="Name"/> and
/// versioned with the assembly's informational version, back every span and measurement the SDK emits. An
/// application observes them by pointing the OpenTelemetry SDK's <c>AddSource(XyoTelemetry.Name)</c> and
/// <c>AddMeter(XyoTelemetry.Name)</c> at this name, or by registering a plain <c>ActivityListener</c> /
/// <c>MeterListener</c> against it directly. See README.md for a minimal registration example.
/// </para>
/// <para>
/// Every instrument here is a static, process-wide singleton. <see cref="ActivitySource"/>'s <c>StartActivity</c>
/// allocates no <see cref="Activity"/> when nothing is listening, and every metric call in this SDK is guarded
/// by the instrument's <c>Enabled</c> property before it builds any tags, which is what keeps this
/// instrumentation safe to leave permanently enabled in a high-volume enrichment pipeline.
/// </para>
/// </remarks>
public static class XyoTelemetry
{
    /// <summary>
    /// The name shared by the SDK's <see cref="ActivitySource"/> and <see cref="Meter"/>.
    /// </summary>
    public const string Name = "Xyo.Sdk";

    private static readonly string Version =
        typeof(XyoTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(XyoTelemetry).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    internal static readonly ActivitySource ActivitySource = new(Name, Version);

    internal static readonly Meter Meter = new(Name, Version);

    /// <summary>
    /// Number of client operations completed, tagged by <c>xyo.sdk.operation</c> and <c>xyo.sdk.outcome</c>.
    /// </summary>
    internal static readonly Counter<long> RequestCount = Meter.CreateCounter<long>(
        "xyo.sdk.client.request.count",
        unit: "{request}",
        description: "Number of client operations completed, tagged by operation and outcome.");

    /// <summary>
    /// Duration of client operations in milliseconds, tagged by <c>xyo.sdk.operation</c> and
    /// <c>xyo.sdk.outcome</c>.
    /// </summary>
    internal static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "xyo.sdk.client.request.duration",
        unit: "ms",
        description: "Duration of client operations in milliseconds, tagged by operation and outcome.");

    /// <summary>
    /// Number of HTTP 429 rate-limit responses received, tagged by <c>xyo.sdk.operation</c>.
    /// </summary>
    internal static readonly Counter<long> RateLimitCount = Meter.CreateCounter<long>(
        "xyo.sdk.client.rate_limit.count",
        unit: "{response}",
        description: "Number of HTTP 429 rate-limit responses received.");

    /// <summary>
    /// Number of archive-download redirects refused by the egress allowlist.
    /// </summary>
    internal static readonly Counter<long> RedirectRefusedCount = Meter.CreateCounter<long>(
        "xyo.sdk.download.redirect_refused.count",
        unit: "{redirect}",
        description: "Number of archive-download redirects refused by the egress allowlist.");

    /// <summary>
    /// Number of times a download safety bound tripped, tagged by <c>xyo.sdk.bound</c> (one of
    /// <c>max_archive_bytes</c>, <c>max_decompressed_bytes</c>, <c>max_entry_bytes</c>, <c>max_tar_entries</c>,
    /// <c>idle_timeout</c>, <c>total_duration</c>, or <c>max_redirects</c>).
    /// </summary>
    internal static readonly Counter<long> DownloadBoundTrippedCount = Meter.CreateCounter<long>(
        "xyo.sdk.download.bound_tripped.count",
        unit: "{event}",
        description: "Number of times a download safety bound (byte, entry, timeout, or redirect) tripped.");
}
