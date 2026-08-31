using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Xyo.Sdk.Client;

/// <summary>
/// Immutable configuration options for initializing the XYO Financial SDK client.
/// </summary>
/// <remarks>
/// As a <c>record</c>, structural equality (<c>==</c>, <see cref="Equals(object?)"/>,
/// <see cref="GetHashCode"/>) compares the configuration as written, not as resolved: a config with
/// <see cref="DownloadConnectTimeout"/> left unset (so it resolves to the ten-minute default) and one with
/// <see cref="DownloadConnectTimeout"/> set explicitly to ten minutes compare unequal, because the two
/// differ in their internal null-vs-set state even though every request they produce behaves identically.
/// The same applies to <see cref="ReadIdleTimeout"/> and the obsolete <see cref="DownloadTimeout"/> alias.
/// Do not rely on structural equality (e.g. as a <c>Dictionary</c> key, or to deduplicate configurations) to
/// mean "these two configs behave the same" for these three members; compare the resolved properties
/// directly instead.
/// </remarks>
public sealed record XyoClientConfig
{
    private const string DefaultProductionUrl = "https://api.xyo.financial";
    private static readonly Regex CrlfRegex = new(@"[\r\n]", RegexOptions.Compiled);
    private static readonly Regex TraceparentRegex = new(
        @"^[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}\z",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Internal rather than private so XyoClientOptions can share the same defaults instead of hardcoding
    // its own copies (see XyoClientOptions.EffectiveDownloadConnectTimeout / EffectiveReadIdleTimeout),
    // which is what let its getters and ToConfig() disagree in the first place.
    internal static readonly TimeSpan DefaultDownloadConnectTimeout = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan DefaultReadIdleTimeout = TimeSpan.FromSeconds(120);

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string? _apiKey;
    private string? _traceparent;
    private TimeSpan? _legacyDownloadTimeout;
    private TimeSpan? _downloadConnectTimeoutOverride;
    private TimeSpan? _readIdleTimeoutOverride;
    // Deliberately NOT validated here: this field initializer runs on every construction, before an
    // explicit `BaseUrl = ...` in an object initializer is applied. Validating eagerly would mean an
    // invalid XYO_API_BASE_URL breaks construction even when the caller overrides BaseUrl explicitly.
    // The env-var-derived default is validated lazily instead, by XyoClient's constructor, at the point
    // where we know definitively whether an override was supplied.
    private string _baseUrl = ResolveDefaultBaseUrl();
    private IReadOnlyDictionary<string, string> _defaultHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _trustedDownloadHosts = Array.Empty<string>();

    /// <summary>
    /// Gets the static API token.
    /// </summary>
    public string? ApiKey => _apiKey;

    /// <summary>
    /// Gets the dynamic asynchronous API key supplier delegate (for secrets managers and token rotation).
    /// </summary>
    /// <remarks>
    /// Invoked on every request that needs a token (no internal caching). If the supplier calls out to a
    /// secrets manager or token service, it must cache/memoize its own result with an appropriate expiry --
    /// otherwise every enrichment call pays that round trip, and at batch throughput that round trip becomes
    /// both a latency multiplier and a throttling risk against the secrets service.
    /// </remarks>
    public Func<CancellationToken, Task<string>>? ApiKeySupplier { get; init; }

    /// <summary>
    /// Gets the target API base URL (e.g. https://api.xyo.financial or sandbox). Must be an absolute HTTPS
    /// URI; plain HTTP is only accepted for loopback hosts, since the Bearer token would otherwise be sent
    /// in cleartext. Validated on every construction path, including <c>init</c> (e.g. binding from
    /// <c>appsettings.json</c> via <see cref="XyoClientOptions"/>), not just <see cref="WithBaseUrl"/>.
    /// </summary>
    public string BaseUrl
    {
        get => _baseUrl;
        init => _baseUrl = NormalizeBaseUrl(value);
    }

    /// <summary>
    /// Gets the optional distributed tracing correlation identifier attached to requests (X-Correlation-ID).
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Gets the optional W3C traceparent header attached to requests (traceparent).
    /// </summary>
    public string? Traceparent
    {
        get => _traceparent;
        init
        {
            if (value != null && CrlfRegex.IsMatch(value))
                throw new ArgumentException("Traceparent header contains illegal CRLF injection characters.", nameof(value));
            _traceparent = value;
        }
    }

    /// <summary>
    /// Gets the timeout duration for a single unary API call (enrichment, batch submit, status lookup).
    /// Enforced independently per call via a linked cancellation token; does not bound archive downloads,
    /// see <see cref="DownloadConnectTimeout"/> and <see cref="ReadIdleTimeout"/>.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the deadline for the connection and redirect phase of an archive download (default 10 minutes):
    /// establishing the HTTP connection, following redirects, and waiting for response headers, for
    /// <see cref="Xyo.Sdk.Client.IXyoClient.StreamEnrichmentCollectionAsync"/> /
    /// <see cref="Xyo.Sdk.Client.IXyoClient.DownloadEnrichmentCollectionAsync"/>. Does not bound time spent
    /// reading the archive body once headers arrive; see <see cref="ReadIdleTimeout"/> for that. Falls back
    /// to the obsolete <see cref="DownloadTimeout"/>'s value when this property is not itself set
    /// explicitly (see that property's remarks).
    /// </summary>
    /// <remarks>
    /// Must be a positive duration, or <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable
    /// the bound entirely. Disabling it deliberately reopens a header-phase slowloris: a peer that accepts
    /// the connection but never returns response headers can hold the call open indefinitely, so a caller
    /// who sets this to <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> must keep
    /// <see cref="MaxTotalDownloadDuration"/> finite, since that becomes the only remaining time bound (and
    /// even then, only after the read phase starts -- see that property's remarks).
    /// </remarks>
    public TimeSpan DownloadConnectTimeout
    {
        get => _downloadConnectTimeoutOverride ?? _legacyDownloadTimeout ?? DefaultDownloadConnectTimeout;
        init => _downloadConnectTimeoutOverride = ValidateDownloadTimeout(value, nameof(DownloadConnectTimeout));
    }

    /// <summary>
    /// Gets the idle stall timeout for a single network read during archive streaming (default 120
    /// seconds), reset on every read. Bounds how long a peer may go without producing the next chunk of the
    /// archive body once response headers have arrived; see <see cref="DownloadConnectTimeout"/> for the
    /// earlier connection/redirect phase. Falls back to the obsolete <see cref="DownloadTimeout"/>'s value
    /// when this property is not itself set explicitly (see that property's remarks).
    /// </summary>
    /// <remarks>
    /// Does not, on its own, bound the transfer as a whole -- see <see cref="MaxTotalDownloadDuration"/> for
    /// why a per-read idle bound alone cannot do that. Must be a positive duration, or
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable the bound entirely; disabling it
    /// also silently defeats <see cref="MaxTotalDownloadDuration"/>, because that budget is only evaluated
    /// after a read returns, so a read that never returns never reaches the check.
    /// </remarks>
    public TimeSpan ReadIdleTimeout
    {
        get => _readIdleTimeoutOverride ?? _legacyDownloadTimeout ?? DefaultReadIdleTimeout;
        init => _readIdleTimeoutOverride = ValidateDownloadTimeout(value, nameof(ReadIdleTimeout));
    }

    /// <summary>
    /// Gets the timeout duration previously applied to both the connection/redirect phase and the per-read
    /// idle stall detection of an archive download.
    /// </summary>
    /// <remarks>
    /// Superseded by <see cref="DownloadConnectTimeout"/> and <see cref="ReadIdleTimeout"/>, which separate
    /// those two unrelated roles: ten minutes is a defensible connection deadline but a very slow stall
    /// detector, so a single value could not be right for both. When set, this property seeds the value of
    /// whichever of <see cref="DownloadConnectTimeout"/> and <see cref="ReadIdleTimeout"/> was not itself
    /// set explicitly, so existing configuration keeps working. Note this is an observable behaviour
    /// change for a caller relying solely on the pre-split default: the effective stall timeout drops from
    /// this property's 10-minute default to <see cref="ReadIdleTimeout"/>'s 120-second default -- see
    /// CHANGELOG.md. Scheduled for removal in the next major version per the versioning policy in
    /// CONTRIBUTING.md.
    /// </remarks>
    [Obsolete("Use DownloadConnectTimeout (connection/redirect deadline) and ReadIdleTimeout (per-read stall timeout) instead. DownloadTimeout still seeds both when set, but conflates two unrelated roles and will be removed in the next major version.")]
    public TimeSpan DownloadTimeout
    {
        get => _legacyDownloadTimeout ?? DefaultDownloadConnectTimeout;
        init => _legacyDownloadTimeout = ValidateDownloadTimeout(value, nameof(DownloadTimeout));
    }

    /// <summary>
    /// Gets the maximum cumulative time an archive transfer may spend waiting on the network, across all
    /// reads (default 1 hour). Time the caller spends processing each yielded record is never counted, so
    /// this bounds the transfer without penalising a slow consumer. Set to
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable.
    /// </summary>
    /// <remarks>
    /// <see cref="ReadIdleTimeout"/> resets on every read, so on its own it bounds nothing cumulative: a peer
    /// delivering a few bytes just inside each idle window keeps the connection and the enumerating task
    /// alive indefinitely, because no individual read ever stalls. The byte bounds
    /// (<see cref="MaxArchiveBytes"/>, <see cref="MaxDecompressedBytes"/>, <see cref="MaxTarEntries"/>) do
    /// not help either, since such a transfer is bounded in bytes and unbounded in time. This is the bound
    /// that turns "a job that neither completes nor fails" into a job that fails. Note the budget is only
    /// checked once a read returns, so a single stalled read can overshoot this bound by up to one
    /// <see cref="ReadIdleTimeout"/> before the overshoot is caught.
    /// </remarks>
    public TimeSpan MaxTotalDownloadDuration { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets the maximum allowed download archive byte size for bulk processing (default 100 MiB).
    /// </summary>
    public long MaxArchiveBytes { get; init; } = 104_857_600; // 100 MiB

    /// <summary>
    /// Gets the maximum total decompressed byte count allowed while inflating an archive (default 2000 MiB,
    /// a 20:1 ratio over <see cref="MaxArchiveBytes"/>). <see cref="MaxArchiveBytes"/> only bounds bytes read
    /// off the wire, before decompression; this bounds the expansion itself, which is what a decompression
    /// bomb (CWE-400) actually attacks.
    /// </summary>
    public long MaxDecompressedBytes { get; init; } = 2_097_152_000; // 2000 MiB

    /// <summary>
    /// Gets the maximum allowed decompressed size per TAR entry (default 10 MiB).
    /// </summary>
    public long MaxEntryBytes { get; init; } = 10_485_760; // 10 MiB

    /// <summary>
    /// Gets the maximum number of entries allowed in a TAR archive (default 50,000).
    /// </summary>
    public int MaxTarEntries { get; init; } = 50_000;

    /// <summary>
    /// Gets the list of additional trusted corporate storage hosts for Zero-Trust download validation.
    /// </summary>
    public IReadOnlyList<string> TrustedDownloadHosts
    {
        get => _trustedDownloadHosts;
        init => _trustedDownloadHosts = value == null
            ? throw new ArgumentNullException(nameof(value))
            : new List<string>(value);
    }

    /// <summary>
    /// Gets custom default headers appended to outbound API requests. Not sent to external archive storage
    /// hosts (see <see cref="Security.DownloadSecurityPolicy.IsExternalStorageHost"/>) for the same reason
    /// the Bearer token is withheld from them.
    /// </summary>
    public IReadOnlyDictionary<string, string> DefaultHeaders
    {
        get => _defaultHeaders;
        init => _defaultHeaders = ValidateDefaultHeaders(value);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XyoClientConfig"/> class.
    /// </summary>
    public XyoClientConfig(string? apiKey = null)
    {
        _apiKey = apiKey;
    }

    /// <summary>
    /// Resolves the current Bearer token, preferring the dynamic token supplier if configured.
    /// </summary>
    public async Task<string> ResolveTokenAsync(CancellationToken cancellationToken = default)
    {
        if (ApiKeySupplier != null)
        {
            var token = await ApiKeySupplier(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            return _apiKey.Trim();
        }

        throw new InvalidOperationException("No API key or dynamic token supplier was configured for XyoClient.");
    }

    /// <summary>
    /// Sets a dynamic asynchronous token supplier delegate.
    /// </summary>
    public XyoClientConfig WithTokenSupplier(Func<CancellationToken, Task<string>> supplier) =>
        this with { ApiKeySupplier = supplier };

    /// <summary>
    /// Sets a synchronous dynamic token supplier delegate.
    /// </summary>
    public XyoClientConfig WithTokenSupplier(Func<string> supplier)
    {
        return WithTokenSupplier(_ => Task.FromResult(supplier()));
    }

    /// <summary>
    /// Sets the target API base URL. Validation is centralized in the <see cref="BaseUrl"/> init accessor.
    /// </summary>
    public XyoClientConfig WithBaseUrl(string baseUrl) => this with { BaseUrl = baseUrl };

    /// <summary>
    /// Attaches a distributed tracing correlation ID header (X-Correlation-ID).
    /// </summary>
    public XyoClientConfig WithCorrelationId(string correlationId)
    {
        if (CrlfRegex.IsMatch(correlationId))
        {
            throw new ArgumentException("Correlation ID contains illegal CRLF injection characters.", nameof(correlationId));
        }

        return this with { CorrelationId = correlationId };
    }

    /// <summary>
    /// Attaches a distributed tracing correlation ID header (X-Correlation-ID) as a <see cref="Guid"/>.
    /// </summary>
    public XyoClientConfig WithCorrelationId(Guid correlationId) => WithCorrelationId(correlationId.ToString());

    /// <summary>
    /// Attaches a W3C traceparent header (traceparent).
    /// </summary>
    public XyoClientConfig WithTraceparent(string traceparent)
    {
        if (CrlfRegex.IsMatch(traceparent))
        {
            throw new ArgumentException("Traceparent header contains illegal CRLF injection characters.", nameof(traceparent));
        }
        if (!TraceparentRegex.IsMatch(traceparent))
        {
            throw new ArgumentException("Traceparent header does not conform to the W3C TraceContext format (version-traceid-parentid-flags).", nameof(traceparent));
        }

        return this with { Traceparent = traceparent };
    }

    /// <summary>
    /// Configures the HTTP request timeout duration.
    /// </summary>
    public XyoClientConfig WithTimeout(TimeSpan timeout) => this with { Timeout = timeout };

    /// <summary>
    /// Adds a trusted corporate internal storage host for Zero-Trust download validation.
    /// </summary>
    public XyoClientConfig AddTrustedDownloadHost(string host)
    {
        if (host == null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        var list = new List<string>(TrustedDownloadHosts) { host.Trim() };
        return this with { TrustedDownloadHosts = list };
    }

    /// <summary>
    /// Adds or updates a default HTTP header attached to outbound requests.
    /// </summary>
    public XyoClientConfig WithDefaultHeader(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Header key cannot be null or empty.", nameof(key));
        }

        if (CrlfRegex.IsMatch(key) || CrlfRegex.IsMatch(value))
        {
            throw new ArgumentException("Header contains forbidden CRLF injection characters.");
        }

        var headers = new Dictionary<string, string>(DefaultHeaders, StringComparer.OrdinalIgnoreCase)
        {
            [key] = value
        };

        return this with { DefaultHeaders = headers };
    }

    /// <summary>
    /// Redacts credentials in debugger inspections and log representations.
    /// </summary>
    public override string ToString()
    {
        string tokenDisplay = string.IsNullOrEmpty(_apiKey) ? "(Dynamic/None)" : "[REDACTED]";
        return $"XyoClientConfig {{ BaseUrl = '{BaseUrl}', ApiKey = '{tokenDisplay}', Timeout = {Timeout.TotalSeconds}s, " +
            $"DownloadConnectTimeout = {DownloadConnectTimeout.TotalSeconds}s, ReadIdleTimeout = {ReadIdleTimeout.TotalSeconds}s, " +
            $"CorrelationId = '{CorrelationId}', Traceparent = '{Traceparent}' }}";
    }

    private static string ResolveDefaultBaseUrl()
    {
        string? envUrl = Environment.GetEnvironmentVariable("XYO_API_BASE_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
        {
            return envUrl.TrimEnd('/');
        }
        return DefaultProductionUrl;
    }

    /// <summary>
    /// Validates and normalises a candidate base URL: must be an absolute URI with no user info, query, or
    /// fragment component, HTTPS unless the host is loopback, with any trailing slash trimmed. Mirrors the
    /// scheme/loopback rules in <see cref="Security.DownloadSecurityPolicy"/> so the same policy governs
    /// where the Bearer token is sent for API calls as for archive downloads.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so <see cref="XyoClient"/> can re-validate the env-var-derived default
    /// lazily at construction time; see the comment on the <c>_baseUrl</c> field initializer above.
    /// </remarks>
    internal static string NormalizeBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL cannot be null or empty.", nameof(baseUrl));
        }

        string trimmed = baseUrl.TrimEnd('/');

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Base URL '{baseUrl}' is not a valid absolute URI.", nameof(baseUrl));
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            bool isHttpLoopback = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && IsLoopbackHost(uri.Host);
            if (!isHttpLoopback)
            {
                throw new ArgumentException(
                    $"Base URL '{baseUrl}' must use HTTPS (plain HTTP is only permitted for loopback hosts, to avoid transmitting the API key in cleartext).",
                    nameof(baseUrl));
            }
        }

        // Request URIs are built by string interpolation of BaseUrl + a path (see XyoClient.cs), not by
        // composing against the parsed Uri. A UserInfo/Query/Fragment component here would be validated
        // against the Uri form but then silently swallow the appended path once reduced back to a string
        // (a trailing '#' turns the whole intended path into a URI fragment, routing every request to the
        // host root) -- so those components are rejected outright rather than validated-then-ignored.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException($"Base URL '{baseUrl}' must not contain user info (e.g. 'user:pass@').", nameof(baseUrl));
        }
        if (!string.IsNullOrEmpty(uri.Query))
        {
            throw new ArgumentException($"Base URL '{baseUrl}' must not contain a query string.", nameof(baseUrl));
        }
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException($"Base URL '{baseUrl}' must not contain a fragment.", nameof(baseUrl));
        }

        return trimmed;
    }

    /// <summary>
    /// Validates an effective base URL and rethrows any failure with the environment-variable hint attached.
    /// </summary>
    /// <param name="baseUrl">The effective base URL to validate.</param>
    /// <param name="propertyPath">Owning property named in the message, e.g. <c>XyoClientOptions.BaseUrl</c>.</param>
    /// <param name="paramName">Parameter name to attach to the thrown <see cref="ArgumentException"/>.</param>
    /// <remarks>
    /// Shared by every construction path (<see cref="XyoClient"/>'s constructor and
    /// <see cref="XyoClientOptions.ToConfig"/>) rather than duplicated, so the diagnostic cannot drift
    /// between them. The hint matters because a value inherited from XYO_API_BASE_URL appears nowhere in the
    /// caller's own code, and the error would otherwise name a URL they cannot find.
    /// </remarks>
    internal static void ValidateEffectiveBaseUrl(string baseUrl, string propertyPath, string paramName)
    {
        try
        {
            NormalizeBaseUrl(baseUrl);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                $"{propertyPath} '{baseUrl}' is invalid: {ex.Message} " +
                "If BaseUrl was not set explicitly, check the XYO_API_BASE_URL environment variable.",
                paramName, ex);
        }
    }

    /// <summary>
    /// Validates a candidate <see cref="DownloadConnectTimeout"/>, <see cref="ReadIdleTimeout"/>, or
    /// <see cref="DownloadTimeout"/> value: it must be a positive duration, or
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to explicitly disable the bound.
    /// </summary>
    /// <remarks>
    /// The message deliberately does not echo <paramref name="value"/>: these are network timeouts, not
    /// secrets, but a caller misconfiguring one from an expression (e.g. a miscomputed
    /// <c>TimeSpan.FromSeconds(-retryCount)</c>) gains nothing from the value being restated, and every
    /// other validated member of this type (<see cref="BaseUrl"/>, <see cref="DefaultHeaders"/>,
    /// <see cref="TrustedDownloadHosts"/>, <see cref="Traceparent"/>) follows the same convention of naming
    /// the property and the constraint, not the offending value.
    /// </remarks>
    private static TimeSpan ValidateDownloadTimeout(TimeSpan value, string propertyName)
    {
        if (value == System.Threading.Timeout.InfiniteTimeSpan)
        {
            // Deliberately allowed: an explicit opt-out of this bound. See the remarks on
            // DownloadConnectTimeout and ReadIdleTimeout for what a caller relying on this must also do.
            return value;
        }

        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                propertyName,
                $"{propertyName} must be a positive duration, or Timeout.InfiniteTimeSpan to disable the bound.");
        }

        return value;
    }

    private static bool IsLoopbackHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase) ||
               (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>
    /// Validates every key/value in a candidate default-headers dictionary for CRLF injection (CWE-113)
    /// and returns a defensive copy. <see cref="WithDefaultHeader"/> already validated the entry it adds;
    /// this closes the same gap for a dictionary assigned directly through the <c>init</c> accessor (e.g.
    /// by <see cref="XyoClientOptions"/>).
    /// </summary>
    /// <remarks>
    /// Copying is not just defence-in-depth: without it, this validated the caller's own dictionary and
    /// then stored a live reference to it (<c>XyoClientOptions.ToConfig()</c> hands over its own
    /// <c>Dictionary</c> by reference), so a caller mutating that dictionary after construction -- e.g.
    /// still holding the <see cref="XyoClientOptions"/> instance used to build a singleton-registered
    /// <see cref="XyoClient"/> -- bypassed validation entirely for every request made afterward. The copy
    /// also normalises the comparer to <see cref="StringComparer.OrdinalIgnoreCase"/> regardless of what
    /// the caller's dictionary used, matching this type's own default.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> ValidateDefaultHeaders(IReadOnlyDictionary<string, string> headers)
    {
        if (headers == null)
        {
            throw new ArgumentNullException(nameof(headers));
        }

        var copy = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            if (CrlfRegex.IsMatch(key) || CrlfRegex.IsMatch(value))
            {
                throw new ArgumentException($"Default header '{key}' contains forbidden CRLF injection characters (CWE-113).", nameof(DefaultHeaders));
            }
            copy[key] = value;
        }

        return copy;
    }
}
