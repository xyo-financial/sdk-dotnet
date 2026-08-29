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
public sealed record XyoClientConfig
{
    private const string DefaultProductionUrl = "https://api.xyo.financial";
    private static readonly Regex CrlfRegex = new(@"[\r\n]", RegexOptions.Compiled);
    private static readonly Regex TraceparentRegex = new(
        @"^[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string? _apiKey;
    private string? _traceparent;
    private string _baseUrl = NormalizeBaseUrl(ResolveDefaultBaseUrl());

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
    /// see <see cref="DownloadTimeout"/>.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the timeout duration for the entire bulk archive download and processing operation
    /// (<see cref="Xyo.Sdk.Client.IXyoClient.StreamEnrichmentCollectionAsync"/> /
    /// <see cref="Xyo.Sdk.Client.IXyoClient.DownloadEnrichmentCollectionAsync"/>), covering all redirect
    /// hops plus the full download and decompression. Kept independent of <see cref="Timeout"/> because a
    /// multi-hundred-MB archive legitimately needs far longer than a single unary call.
    /// </summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);

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
    public IReadOnlyList<string> TrustedDownloadHosts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets custom default headers appended to outbound requests.
    /// </summary>
    public IReadOnlyDictionary<string, string> DefaultHeaders { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
        return $"XyoClientConfig {{ BaseUrl = '{BaseUrl}', ApiKey = '{tokenDisplay}', Timeout = {Timeout.TotalSeconds}s, CorrelationId = '{CorrelationId}', Traceparent = '{Traceparent}' }}";
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
    /// Validates and normalizes a candidate base URL: must be an absolute URI, HTTPS unless the host is
    /// loopback, with any trailing slash trimmed. Mirrors the scheme/loopback rules in
    /// <see cref="Security.DownloadSecurityPolicy"/> so the same policy governs where the Bearer token is
    /// sent for API calls as for archive downloads.
    /// </summary>
    private static string NormalizeBaseUrl(string baseUrl)
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

        return trimmed;
    }

    private static bool IsLoopbackHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase) ||
               (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    }
}
