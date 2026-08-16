using System;
using System.Collections.Generic;
using Xyo.Sdk.Exceptions;

namespace Xyo.Sdk.Security;

/// <summary>
/// Enforces Zero-Trust egress domain validation (CWE-183) and Server-Side Request Forgery (SSRF) defense on archive downloads.
/// </summary>
public class DownloadSecurityPolicy
{
    private static readonly HashSet<string> DefaultTrustedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.xyo.financial",
        "download.xyo.financial",
        "xyo-financial.s3.amazonaws.com",
        "xyo-financial.s3.us-east-1.amazonaws.com"
    };

    private readonly HashSet<string> _trustedHosts;
    private readonly string? _configuredApiHost;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadSecurityPolicy"/> class.
    /// </summary>
    /// <param name="configuredApiBaseUrl">The configured base URL of the API gateway.</param>
    /// <param name="customTrustedHosts">Additional corporate internal storage hosts allowed for egress.</param>
    public DownloadSecurityPolicy(string? configuredApiBaseUrl = null, IEnumerable<string>? customTrustedHosts = null)
    {
        _trustedHosts = new HashSet<string>(DefaultTrustedHosts, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(configuredApiBaseUrl) && Uri.TryCreate(configuredApiBaseUrl, UriKind.Absolute, out var apiUri))
        {
            _configuredApiHost = apiUri.Host;
            _trustedHosts.Add(apiUri.Host);
        }

        if (customTrustedHosts != null)
        {
            foreach (var host in customTrustedHosts)
            {
                if (!string.IsNullOrWhiteSpace(host))
                {
                    _trustedHosts.Add(host.Trim());
                }
            }
        }
    }

    /// <summary>
    /// Validates the target download URL against scheme, host, and domain pinning policies.
    /// </summary>
    /// <param name="downloadUrl">The candidate download URL.</param>
    /// <returns>The validated absolute URI.</returns>
    /// <exception cref="XyoClientException">Thrown if the URL violates scheme or host validation rules.</exception>
    public Uri ValidateDownloadUrl(string downloadUrl)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new XyoClientException(System.Net.HttpStatusCode.BadRequest, "Download URL cannot be null, empty, or whitespace.");
        }

        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
        {
            throw new XyoClientException(System.Net.HttpStatusCode.BadRequest, "Download URL is not a valid absolute URI.");
        }

        // Scheme validation - reject cleartext HTTP, file://, ftp://, gopher://
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                // Reject insecure HTTP for remote targets
                bool isLocalhost = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
                if (!isLocalhost)
                {
                    throw new XyoClientException(System.Net.HttpStatusCode.BadRequest, "Insecure HTTP scheme rejected for remote archive download. HTTPS is strictly mandated.");
                }
            }
            else
            {
                throw new XyoClientException(System.Net.HttpStatusCode.BadRequest, $"Unsupported URI scheme '{uri.Scheme}' rejected for download archive.");
            }
        }

        // Host validation - Zero-Trust allowlist matching
        string host = uri.Host;
        if (!_trustedHosts.Contains(host))
        {
            throw new XyoClientException(System.Net.HttpStatusCode.BadRequest,
                $"Target download host '{host}' is not in the trusted domain allowlist. Register host via ClientConfig if using dedicated private storage.");
        }

        return uri;
    }

    /// <summary>
    /// Checks if a given host is a third-party or S3 storage host where Authorization Bearer headers should be stripped.
    /// </summary>
    public bool IsExternalStorageHost(string host)
    {
        if (!string.IsNullOrWhiteSpace(_configuredApiHost) && string.Equals(host, _configuredApiHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(host, "api.xyo.financial", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
