using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;

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

    private static readonly string Version = ResolveInstrumentationVersion();

    private static string ResolveInstrumentationVersion()
    {
        string raw =
            typeof(XyoTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(XyoTelemetry).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        // SourceLink (PublishRepositoryUrl/EmbedUntrackedSources, see the csproj) appends
        // "+{SourceRevisionId}" to the informational version. Left in place, that would make the
        // instrumentation scope -- and therefore every metric stream's identity -- change on every commit,
        // rather than only on every package release. The scope version must track the package version only.
        int plus = raw.IndexOf('+');
        return plus < 0 ? raw : raw[..plus];
    }

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

    /// <summary>
    /// Tag key attached to <see cref="DownloadBoundTrippedCount"/> identifying which bound tripped.
    /// </summary>
    internal const string BoundTagKey = "xyo.sdk.bound";

    // The seven closed-set values for BoundTagKey. Defined once here, structurally, rather than derived
    // from any human-readable exception or log message text at the call site: a message can be reworded
    // for readability without silently reclassifying which bound tripped in a dashboard.
    internal const string BoundMaxArchiveBytes = "max_archive_bytes";
    internal const string BoundMaxDecompressedBytes = "max_decompressed_bytes";
    internal const string BoundMaxEntryBytes = "max_entry_bytes";
    internal const string BoundMaxTarEntries = "max_tar_entries";
    internal const string BoundIdleTimeout = "idle_timeout";
    internal const string BoundTotalDuration = "total_duration";
    internal const string BoundMaxRedirects = "max_redirects";

    private static readonly object[] BoxedStatusCodes = InitializeBoxedStatusCodes();

    private static object[] InitializeBoxedStatusCodes()
    {
        var array = new object[500];
        for (int i = 0; i < array.Length; i++)
        {
            array[i] = 100 + i;
        }
        return array;
    }

    /// <summary>
    /// Returns a pre-allocated boxed <see cref="int"/> object for standard HTTP status codes (100..599)
    /// to eliminate boxing allocations when setting tags on an <see cref="Activity"/>.
    /// </summary>
    /// <param name="statusCode">The HTTP status code as an integer.</param>
    /// <returns>A boxed <see cref="int"/> object representing the status code.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static object GetBoxedStatusCode(int statusCode)
    {
        int index = statusCode - 100;
        if ((uint)index < (uint)BoxedStatusCodes.Length)
        {
            return BoxedStatusCodes[index];
        }

        return statusCode;
    }

    /// <summary>
    /// Returns a pre-allocated boxed <see cref="int"/> object for standard HTTP status codes (100..599)
    /// to eliminate boxing allocations when setting tags on an <see cref="Activity"/>.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>A boxed <see cref="int"/> object representing the status code.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static object GetBoxedStatusCode(HttpStatusCode statusCode) => GetBoxedStatusCode((int)statusCode);
}
