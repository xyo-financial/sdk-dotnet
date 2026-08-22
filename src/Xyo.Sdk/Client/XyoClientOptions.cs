using System;
using System.Collections.Generic;

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
    /// Gets or sets the target API base URL (defaults to https://api.xyo.financial).
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.xyo.financial";

    /// <summary>
    /// Gets or sets the default correlation ID (X-Correlation-ID).
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets the default W3C traceparent header (traceparent).
    /// </summary>
    public string? Traceparent { get; set; }

    /// <summary>
    /// Gets or sets the HTTP request timeout duration (default 30 seconds).
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum allowed download archive byte size for bulk processing (default 100 MiB).
    /// </summary>
    public long MaxArchiveBytes { get; set; } = 104_857_600;

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
    /// Converts this options instance to an immutable <see cref="XyoClientConfig"/>.
    /// </summary>
    public XyoClientConfig ToConfig()
    {
        return new XyoClientConfig(ApiKey)
        {
            BaseUrl = BaseUrl,
            CorrelationId = CorrelationId,
            Traceparent = Traceparent,
            Timeout = Timeout,
            MaxArchiveBytes = MaxArchiveBytes,
            MaxEntryBytes = MaxEntryBytes,
            MaxTarEntries = MaxTarEntries,
            TrustedDownloadHosts = TrustedDownloadHosts
        };
    }
}
