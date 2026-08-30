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
    /// archive downloads, see <see cref="DownloadTimeout"/>.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the timeout duration for archive download and stream processing (default 10 minutes),
    /// independent of <see cref="Timeout"/>. Enforces a deadline on initial HTTP connection/redirects and
    /// acts as a per-read idle stall timeout during stream decompression.
    /// </summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(10);

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
    /// Converts this options instance to an immutable <see cref="XyoClientConfig"/>.
    /// </summary>
    public XyoClientConfig ToConfig()
    {
        return new XyoClientConfig(ApiKey)
        {
            ApiKeySupplier = ApiKeySupplier,
            BaseUrl = BaseUrl,
            CorrelationId = CorrelationId,
            Traceparent = Traceparent,
            Timeout = Timeout,
            DownloadTimeout = DownloadTimeout,
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
