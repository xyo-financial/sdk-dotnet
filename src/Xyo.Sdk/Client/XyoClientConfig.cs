using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Xyo.Sdk.Client;

/// <summary>
/// Immutable configuration options for initializing the XYO Financial SDK client.
/// </summary>
public sealed class XyoClientConfig
{
    private const string DefaultProductionUrl = "https://api.xyo.financial";
    private static readonly Regex CrlfRegex = new(@"[\r\n]", RegexOptions.Compiled);
    private static readonly Regex TraceparentRegex = new(
        @"^[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string? _apiKey;
    private string? _traceparent;

    /// <summary>
    /// Gets the static API token.
    /// </summary>
    public string? ApiKey => _apiKey;

    /// <summary>
    /// Gets the dynamic asynchronous API key supplier delegate (for secrets managers and token rotation).
    /// </summary>
    public Func<CancellationToken, Task<string>>? ApiKeySupplier { get; init; }

    /// <summary>
    /// Gets the target API base URL (e.g. https://api.xyo.financial or sandbox).
    /// </summary>
    public string BaseUrl { get; init; } = ResolveDefaultBaseUrl();

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
    /// Gets the HTTP request timeout duration.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the maximum allowed download archive byte size for bulk processing (default 100 MiB).
    /// </summary>
    public long MaxArchiveBytes { get; init; } = 104_857_600; // 100 MiB

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
    public XyoClientConfig WithTokenSupplier(Func<CancellationToken, Task<string>> supplier)
    {
        return new XyoClientConfig(_apiKey)
        {
            ApiKeySupplier = supplier,
            BaseUrl = BaseUrl,
            CorrelationId = CorrelationId,
            Traceparent = Traceparent,
            Timeout = Timeout,
            MaxArchiveBytes = MaxArchiveBytes,
            MaxEntryBytes = MaxEntryBytes,
            MaxTarEntries = MaxTarEntries,
            TrustedDownloadHosts = TrustedDownloadHosts,
            DefaultHeaders = DefaultHeaders
        };
    }

    /// <summary>
    /// Sets a synchronous dynamic token supplier delegate.
    /// </summary>
    public XyoClientConfig WithTokenSupplier(Func<string> supplier)
    {
        return WithTokenSupplier(_ => Task.FromResult(supplier()));
    }

    /// <summary>
    /// Sets the target API base URL.
    /// </summary>
    public XyoClientConfig WithBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL cannot be null or empty.", nameof(baseUrl));
        }

        return new XyoClientConfig(_apiKey)
        {
            ApiKeySupplier = ApiKeySupplier,
            BaseUrl = baseUrl.TrimEnd('/'),
            CorrelationId = CorrelationId,
            Traceparent = Traceparent,
            Timeout = Timeout,
            MaxArchiveBytes = MaxArchiveBytes,
            MaxEntryBytes = MaxEntryBytes,
            MaxTarEntries = MaxTarEntries,
            TrustedDownloadHosts = TrustedDownloadHosts,
            DefaultHeaders = DefaultHeaders
        };
    }

    /// <summary>
    /// Attaches a distributed tracing correlation ID header (X-Correlation-ID).
    /// </summary>
    public XyoClientConfig WithCorrelationId(string correlationId)
    {
        if (CrlfRegex.IsMatch(correlationId))
        {
            throw new ArgumentException("Correlation ID contains illegal CRLF injection characters.", nameof(correlationId));
        }

        return new XyoClientConfig(_apiKey)
        {
            ApiKeySupplier = ApiKeySupplier,
            BaseUrl = BaseUrl,
            CorrelationId = correlationId,
            Traceparent = Traceparent,
            Timeout = Timeout,
            MaxArchiveBytes = MaxArchiveBytes,
            MaxEntryBytes = MaxEntryBytes,
            MaxTarEntries = MaxTarEntries,
            TrustedDownloadHosts = TrustedDownloadHosts,
            DefaultHeaders = DefaultHeaders
        };
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

        return new XyoClientConfig(_apiKey)
        {
            ApiKeySupplier = ApiKeySupplier,
            BaseUrl = BaseUrl,
            CorrelationId = CorrelationId,
            Traceparent = traceparent,
            Timeout = Timeout,
            MaxArchiveBytes = MaxArchiveBytes,
            MaxEntryBytes = MaxEntryBytes,
            MaxTarEntries = MaxTarEntries,
            TrustedDownloadHosts = TrustedDownloadHosts,
            DefaultHeaders = DefaultHeaders
        };
    }

    /// <summary>
    /// Configures the HTTP request timeout duration.
    /// </summary>
    public XyoClientConfig WithTimeout(TimeSpan timeout)
    {
        return new XyoClientConfig(_apiKey)
        {
            ApiKeySupplier = ApiKeySupplier,
            BaseUrl = BaseUrl,
            CorrelationId = CorrelationId,
            Traceparent = Traceparent,
            Timeout = timeout,
            MaxArchiveBytes = MaxArchiveBytes,
            MaxEntryBytes = MaxEntryBytes,
            MaxTarEntries = MaxTarEntries,
            TrustedDownloadHosts = TrustedDownloadHosts,
            DefaultHeaders = DefaultHeaders
        };
    }

    /// <summary>
    /// Adds a trusted corporate internal storage host for Zero-Trust download validation.
    /// </summary>
    public XyoClientConfig AddTrustedDownloadHost(string host)
    {
        var list = new List<string>(TrustedDownloadHosts) { host.Trim() };
        return new XyoClientConfig(_apiKey)
        {
            ApiKeySupplier = ApiKeySupplier,
            BaseUrl = BaseUrl,
            CorrelationId = CorrelationId,
            Traceparent = Traceparent,
            Timeout = Timeout,
            MaxArchiveBytes = MaxArchiveBytes,
            MaxEntryBytes = MaxEntryBytes,
            MaxTarEntries = MaxTarEntries,
            TrustedDownloadHosts = list,
            DefaultHeaders = DefaultHeaders
        };
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

        return new XyoClientConfig(_apiKey)
        {
            ApiKeySupplier = ApiKeySupplier,
            BaseUrl = BaseUrl,
            CorrelationId = CorrelationId,
            Traceparent = Traceparent,
            Timeout = Timeout,
            MaxArchiveBytes = MaxArchiveBytes,
            MaxEntryBytes = MaxEntryBytes,
            MaxTarEntries = MaxTarEntries,
            TrustedDownloadHosts = TrustedDownloadHosts,
            DefaultHeaders = headers
        };
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
}
