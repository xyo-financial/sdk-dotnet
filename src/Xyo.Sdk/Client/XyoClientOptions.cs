using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Xyo.Sdk.Client;

/// <summary>
/// Options class for configuring <see cref="XyoClient"/> via ASP.NET Core and Microsoft.Extensions.DependencyInjection.
/// </summary>
public sealed class XyoClientOptions
{
    private TimeSpan? _downloadTimeout;
    private TimeSpan? _downloadConnectTimeout;
    private TimeSpan? _readIdleTimeout;

    /// <summary>
    /// Gets or sets the static API key.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the dynamic asynchronous API key supplier delegate (for secrets managers and token
    /// rotation). See the equivalent member on <see cref="XyoClientConfig.ApiKeySupplier"/> for the
    /// caching contract it must uphold.
    /// </summary>
    public Func<CancellationToken, Task<string>>? ApiKeySupplier { get; set; }

    /// <summary>
    /// Gets or sets the target API base URL. Defaults to the XYO_API_BASE_URL environment variable when
    /// set, falling back to https://api.xyo.financial -- matching <see cref="XyoClientConfig"/>'s default so
    /// DI-registered clients don't silently lose environment-based redirection.
    /// </summary>
    public string BaseUrl { get; set; } = ResolveDefaultBaseUrl();

    /// <summary>
    /// Gets or sets the default correlation ID (X-Correlation-ID).
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets the default W3C traceparent header (traceparent).
    /// </summary>
    public string? Traceparent { get; set; }

    /// <summary>
    /// Gets or sets the timeout duration for a single unary API call (default 30 seconds). Does not bound
    /// archive downloads, see <see cref="DownloadConnectTimeout"/> and <see cref="ReadIdleTimeout"/>.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the deadline for the connection and redirect phase of an archive download (default 10
    /// minutes): establishing the HTTP connection, following redirects, and waiting for response headers.
    /// Does not bound time spent reading the archive body once headers arrive; see
    /// <see cref="ReadIdleTimeout"/> for that. Falls back to the obsolete <see cref="DownloadTimeout"/>'s
    /// value when this property is not itself set explicitly, exactly as <see cref="ToConfig"/> resolves it
    /// -- both share <see cref="EffectiveDownloadConnectTimeout"/>, so this getter never disagrees with the
    /// value <see cref="ToConfig"/> makes effective. Not validated until <see cref="ToConfig"/> is called:
    /// see that method's remarks.
    /// </summary>
    public TimeSpan DownloadConnectTimeout
    {
        get => EffectiveDownloadConnectTimeout;
        set => _downloadConnectTimeout = value;
    }

    /// <summary>
    /// Gets or sets the idle stall timeout for a single network read during archive streaming (default 120
    /// seconds), reset on every read. See <see cref="DownloadConnectTimeout"/> for the earlier
    /// connection/redirect phase, including the fallback and validation-timing notes that apply here too.
    /// </summary>
    public TimeSpan ReadIdleTimeout
    {
        get => EffectiveReadIdleTimeout;
        set => _readIdleTimeout = value;
    }

    /// <summary>
    /// Gets or sets the timeout duration previously applied to both the connection/redirect phase and the
    /// per-read idle stall detection of an archive download. Superseded by
    /// <see cref="DownloadConnectTimeout"/> and <see cref="ReadIdleTimeout"/>, which separate those two
    /// unrelated roles. When set, it seeds the value of whichever of those two properties is not itself set
    /// explicitly, via <see cref="ToConfig"/>, so existing configuration keeps working. Scheduled for
    /// removal in the next major version per the versioning policy in CONTRIBUTING.md.
    /// </summary>
    [Obsolete("Use DownloadConnectTimeout (connection/redirect deadline) and ReadIdleTimeout (per-read stall timeout) instead. DownloadTimeout still seeds both when set, but conflates two unrelated roles and will be removed in the next major version.")]
    public TimeSpan DownloadTimeout
    {
        get => _downloadTimeout ?? XyoClientConfig.DefaultDownloadConnectTimeout;
        set => _downloadTimeout = value;
    }

    /// <summary>
    /// Gets or sets the maximum cumulative time an archive transfer may spend waiting on the network across
    /// all reads (default 1 hour). See <see cref="XyoClientConfig.MaxTotalDownloadDuration"/> for why the
    /// per-read idle timeout alone does not bound the transfer as a whole.
    /// </summary>
    public TimeSpan MaxTotalDownloadDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets the maximum allowed download archive byte size for bulk processing (default 100 MiB).
    /// </summary>
    public long MaxArchiveBytes { get; set; } = 104_857_600;

    /// <summary>
    /// Gets or sets the maximum total decompressed byte count allowed while inflating an archive (default
    /// 2000 MiB). Bounds the expansion itself, independent of <see cref="MaxArchiveBytes"/> which only bounds
    /// bytes read off the wire before decompression.
    /// </summary>
    public long MaxDecompressedBytes { get; set; } = 2_097_152_000;

    /// <summary>
    /// Gets or sets the maximum allowed size per extracted TAR entry (default 10 MiB).
    /// </summary>
    public long MaxEntryBytes { get; set; } = 10_485_760;

    /// <summary>
    /// Gets or sets the maximum number of entries allowed in a TAR archive (default 50,000).
    /// </summary>
    public int MaxTarEntries { get; set; } = 50_000;

    /// <summary>
    /// Gets or sets additional trusted corporate storage hosts for Zero-Trust download allowlisting.
    /// </summary>
    public List<string> TrustedDownloadHosts { get; set; } = new();

    /// <summary>
    /// Gets or sets custom default headers appended to outbound API requests.
    /// </summary>
    public Dictionary<string, string> DefaultHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The single expression of the DownloadConnectTimeout fallback rule (explicit value, else the obsolete
    /// <see cref="DownloadTimeout"/> seed if the caller set it, else the shared default), shared by the
    /// <see cref="DownloadConnectTimeout"/> getter and <see cref="ToConfig"/> so the two cannot drift the
    /// way they previously did: reading <see cref="DownloadConnectTimeout"/> used to return the hardcoded
    /// default even when <see cref="DownloadTimeout"/> had been set, while <see cref="ToConfig"/> correctly
    /// applied the seed -- the same property reading back a different value from the one that took effect.
    /// </summary>
    private TimeSpan EffectiveDownloadConnectTimeout =>
        _downloadConnectTimeout ?? _downloadTimeout ?? XyoClientConfig.DefaultDownloadConnectTimeout;

    /// <summary>
    /// The ReadIdleTimeout equivalent of <see cref="EffectiveDownloadConnectTimeout"/>; see its remarks.
    /// </summary>
    private TimeSpan EffectiveReadIdleTimeout =>
        _readIdleTimeout ?? _downloadTimeout ?? XyoClientConfig.DefaultReadIdleTimeout;

    /// <summary>
    /// Converts this options instance to an immutable <see cref="XyoClientConfig"/>.
    /// </summary>
    /// <remarks>
    /// Validation of <see cref="DownloadConnectTimeout"/> and <see cref="ReadIdleTimeout"/> (positive, or
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) happens here, via the assignments below
    /// routing through <see cref="XyoClientConfig"/>'s own <c>init</c> accessors, rather than being
    /// duplicated on this type's setters. That keeps the constraint expressed in exactly one place.
    /// </remarks>
    public XyoClientConfig ToConfig()
    {
        // Validated here rather than left to the BaseUrl init accessor below, so a bad value carries the
        // same XYO_API_BASE_URL hint the XyoClient constructor attaches on the non-DI path. Without this,
        // ToConfig throws from the init accessor before XyoClient's constructor ever runs, and the DI path
        // (the primary one for hosted applications) reports only "must use HTTPS" against a URL that
        // appears nowhere in the caller's own code or configuration files.
        // This does mean BaseUrl is normalised twice: once here and once by the init accessor below.
        // That is deliberate. The alternative, catching around the object initialiser, has to filter on
        // ParamName to avoid reporting a DefaultHeaders CRLF rejection as a BaseUrl problem, and the
        // saving is one Uri.TryCreate per config construction (once per process for a DI singleton).
        XyoClientConfig.ValidateEffectiveBaseUrl(BaseUrl, "XyoClientOptions.BaseUrl", nameof(BaseUrl));

        // DownloadConnectTimeout/ReadIdleTimeout are assigned the already-resolved Effective* values, not
        // the raw nullable fields, and never DownloadTimeout itself: EffectiveDownloadConnectTimeout and
        // EffectiveReadIdleTimeout each already fold in the obsolete DownloadTimeout seed (only when the
        // caller actually set it) ahead of the shared default, so there is exactly one place -- not three --
        // that knows the fallback order. This also means the obsolete DownloadTimeout property on
        // XyoClientConfig itself is never touched here, so no CS0618 suppression is needed in this method.
        return new XyoClientConfig(ApiKey)
        {
            ApiKeySupplier = ApiKeySupplier,
            BaseUrl = BaseUrl,
            CorrelationId = CorrelationId,
            Traceparent = Traceparent,
            Timeout = Timeout,
            DownloadConnectTimeout = EffectiveDownloadConnectTimeout,
            ReadIdleTimeout = EffectiveReadIdleTimeout,
            MaxTotalDownloadDuration = MaxTotalDownloadDuration,
            MaxArchiveBytes = MaxArchiveBytes,
            MaxDecompressedBytes = MaxDecompressedBytes,
            MaxEntryBytes = MaxEntryBytes,
            MaxTarEntries = MaxTarEntries,
            TrustedDownloadHosts = TrustedDownloadHosts,
            DefaultHeaders = DefaultHeaders
        };
    }

    private static string ResolveDefaultBaseUrl()
    {
        string? envUrl = Environment.GetEnvironmentVariable("XYO_API_BASE_URL");
        return string.IsNullOrWhiteSpace(envUrl) ? "https://api.xyo.financial" : envUrl;
    }
}
