using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xyo.Generated.Api;
using Xyo.Generated.Client;
using Xyo.Generated.Model;
using Xyo.Sdk.Exceptions;
using Xyo.Sdk.Security;
using Xyo.Sdk.Streaming;

namespace Xyo.Sdk.Client;

/// <summary>
/// Production-grade C# client for the XYO Financial AI Transaction Enrichment API.
/// </summary>
public class XyoClient : IXyoClient
{
    private static readonly Regex CrlfRegex = new(@"[\r\n]", RegexOptions.Compiled);
    private static readonly Regex CountryCodeRegex = new(@"^[A-Za-z]{2}$", RegexOptions.Compiled);

    private static readonly System.Text.Json.JsonSerializerOptions DefaultJsonOptions = CreateJsonSerializerOptions();

    private static System.Text.Json.JsonSerializerOptions CreateJsonSerializerOptions()
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        options.Converters.Add(new DateTimeJsonConverter());
        options.Converters.Add(new DateTimeNullableJsonConverter());
        options.Converters.Add(new DateOnlyJsonConverter());
        options.Converters.Add(new DateOnlyNullableJsonConverter());
        options.Converters.Add(new APIErrorJsonConverter());
        options.Converters.Add(new EnrichTransactionCollectionResponseJsonConverter());
        options.Converters.Add(new EnrichTransactionsRequestInnerJsonConverter());
        options.Converters.Add(new EnrichmentCollectionStatusResponseJsonConverter());
        options.Converters.Add(new EnrichmentRequestJsonConverter());
        options.Converters.Add(new EnrichmentResponseJsonConverter());
        options.Converters.Add(new ErrorResponseJsonConverter());
        return options;
    }
    private readonly XyoClientConfig _config;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly DownloadSecurityPolicy _securityPolicy;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="XyoClient"/> class with an API key.
    /// </summary>
    public XyoClient(string apiKey) : this(new XyoClientConfig(apiKey))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XyoClient"/> class with a custom configuration.
    /// </summary>
    public XyoClient(XyoClientConfig config, HttpClient? httpClient = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _securityPolicy = new DownloadSecurityPolicy(_config.BaseUrl, _config.TrustedDownloadHosts);

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };
            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = _config.Timeout
            };
            _ownsHttpClient = true;
        }
    }

    /// <inheritdoc />
    public Task<EnrichmentResponse> EnrichTransactionAsync(string content, string countryCode, CancellationToken cancellationToken = default)
    {
        ValidateTransactionInput(content, countryCode, out string normalizedCountryCode);
        var request = new EnrichmentRequest(content: content, countryCode: normalizedCountryCode);
        return EnrichTransactionAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EnrichmentResponse> EnrichTransactionAsync(EnrichmentRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ValidateTransactionInput(request.Content, request.CountryCode, out string normalizedCountryCode);
        request.CountryCode = normalizedCountryCode;

        string token = await _config.ResolveTokenAsync(cancellationToken).ConfigureAwait(false);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/v1/ai/finance/enrichment/transaction");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        ApplyDefaultHeaders(httpRequest);

        httpRequest.Content = JsonContent.Create(request, options: DefaultJsonOptions);

        using var response = await SendRequestAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);

        var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await System.Text.Json.JsonSerializer.DeserializeAsync<EnrichmentResponse>(responseStream, DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(false);

        return result ?? throw new XyoServerException(response.StatusCode, "API server returned an empty payload.");
    }

    /// <inheritdoc />
    public async Task<EnrichTransactionCollectionResponse> EnrichTransactionsAsync(
        IEnumerable<EnrichmentRequest> requests,
        string? apiUser = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (requests == null)
        {
            throw new ArgumentNullException(nameof(requests));
        }

        var requestList = requests.ToList();
        if (requestList.Count == 0)
        {
            throw new XyoClientException(HttpStatusCode.BadRequest, "Transaction collection batch cannot be empty.");
        }

        for (int i = 0; i < requestList.Count; i++)
        {
            var item = requestList[i];
            if (item == null)
            {
                throw new XyoClientException(HttpStatusCode.BadRequest, $"Transaction item at index {i} cannot be null.");
            }
            ValidateTransactionInput(item.Content, item.CountryCode, out string normalized);
            item.CountryCode = normalized;
        }

        ValidateApiUser(apiUser);

        string token = await _config.ResolveTokenAsync(cancellationToken).ConfigureAwait(false);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/v1/ai/finance/enrichment/transactions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(apiUser))
        {
            httpRequest.Headers.Add("x-api-user", apiUser.Trim());
        }

        ApplyDefaultHeaders(httpRequest);

        httpRequest.Content = JsonContent.Create(requestList, options: DefaultJsonOptions);

        using var response = await SendRequestAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);

        var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await System.Text.Json.JsonSerializer.DeserializeAsync<EnrichTransactionCollectionResponse>(responseStream, DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(false);

        return result ?? throw new XyoServerException(response.StatusCode, "API server returned an empty batch response.");
    }

    /// <inheritdoc />
    public async Task<EnrichmentCollectionStatusResponse> GetEnrichmentStatusAsync(
        string id,
        string? apiUser = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new XyoClientException(HttpStatusCode.BadRequest, "Enrichment job identifier cannot be null, empty, or whitespace.");
        }

        ValidateApiUser(apiUser);

        string token = await _config.ResolveTokenAsync(cancellationToken).ConfigureAwait(false);

        var uriBuilder = new UriBuilder($"{_config.BaseUrl}/v1/ai/finance/enrichment/transaction/collection/status")
        {
            Query = $"id={Uri.EscapeDataString(id.Trim())}"
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(apiUser))
        {
            httpRequest.Headers.Add("x-api-user", apiUser.Trim());
        }

        ApplyDefaultHeaders(httpRequest);

        using var response = await SendRequestAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);

        var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await System.Text.Json.JsonSerializer.DeserializeAsync<EnrichmentCollectionStatusResponse>(responseStream, DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(false);

        return result ?? throw new XyoServerException(response.StatusCode, "API server returned an empty status response.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Memory Warning:</b> Buffers all deserialized enrichment records into an in-memory list on the heap.
    /// For high-volume pipelines or large datasets, prefer <see cref="StreamEnrichmentCollectionAsync"/> for streaming processing with an $O(1)$ memory footprint.
    /// </remarks>
    public async Task<IReadOnlyList<EnrichmentResponse>> DownloadEnrichmentCollectionAsync(string downloadUrl, CancellationToken cancellationToken = default)
    {
        var list = new List<EnrichmentResponse>();
        await foreach (var item in StreamEnrichmentCollectionAsync(downloadUrl, cancellationToken).ConfigureAwait(false))
        {
            list.Add(item);
        }
        return list;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EnrichmentResponse> StreamEnrichmentCollectionAsync(
        string downloadUrl,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        Uri validatedUri = _securityPolicy.ValidateDownloadUrl(downloadUrl);

        var httpRequest = new HttpRequestMessage(HttpMethod.Get, validatedUri);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/gzip"));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-tar"));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream", 0.9));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));

        // Attach Authorization only if NOT an external S3 / storage host
        if (!_securityPolicy.IsExternalStorageHost(validatedUri.Host))
        {
            string token = await _config.ResolveTokenAsync(cancellationToken).ConfigureAwait(false);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        ApplyDefaultHeaders(httpRequest);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new XyoNetworkException($"Network request timed out after {_config.Timeout.TotalSeconds} seconds.", ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new XyoNetworkException($"Download transport failure: {ex.Message}", ex);
        }

        using (response)
        {
            await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);

            using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await foreach (var record in TarStreamReader.StreamArchiveAsync(
                responseStream,
                _config.MaxArchiveBytes,
                _config.MaxEntryBytes,
                _config.MaxTarEntries,
                cancellationToken).ConfigureAwait(false))
            {
                yield return record;
            }
        }
    }

    private void ApplyDefaultHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_config.CorrelationId) && !request.Headers.Contains("X-Correlation-ID"))
        {
            request.Headers.Add("X-Correlation-ID", _config.CorrelationId);
        }

        foreach (var (key, value) in _config.DefaultHeaders)
        {
            if (!request.Headers.Contains(key))
            {
                request.Headers.Add(key, value);
            }
        }
    }

    private async Task<HttpResponseMessage> SendRequestAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new XyoNetworkException($"Network request timed out after {_config.Timeout.TotalSeconds} seconds.", ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new XyoNetworkException($"Network connection failed while calling {request.RequestUri}: {ex.Message}", ex);
        }
    }

    private static async Task EnsureSuccessResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string rawPayload = string.Empty;
        if (response.Content != null)
        {
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);
                const int maxChars = 32768;
                char[] charBuffer = new char[maxChars];
                int totalCharsRead = await reader.ReadBlockAsync(charBuffer.AsMemory(0, maxChars), cancellationToken).ConfigureAwait(false);
                rawPayload = new string(charBuffer, 0, totalCharsRead);
            }
            catch
            {
                // Ignored
            }
        }

        int statusCodeInt = (int)response.StatusCode;

        if (statusCodeInt >= 500)
        {
            string msg = !string.IsNullOrWhiteSpace(rawPayload) ? rawPayload : $"[HTTP {statusCodeInt}] Upstream server error.";
            throw new XyoServerException(response.StatusCode, msg, rawPayload);
        }

        if (statusCodeInt >= 400)
        {
            if (!string.IsNullOrWhiteSpace(rawPayload) && (rawPayload.TrimStart().StartsWith('{') || rawPayload.TrimStart().StartsWith('[')))
            {
                throw XyoProblemDetailsException.FromJson(response.StatusCode, rawPayload);
            }

            string msg = !string.IsNullOrWhiteSpace(rawPayload) ? rawPayload : $"[HTTP {statusCodeInt}] Client error.";
            throw new XyoClientException(response.StatusCode, msg, rawPayload);
        }

        throw new XyoClientException(response.StatusCode, $"[HTTP {statusCodeInt}] Unexpected HTTP response.", rawPayload);
    }

    private static void ValidateTransactionInput(string content, string countryCode, out string normalizedCountryCode)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new XyoClientException(HttpStatusCode.BadRequest, "Transaction content cannot be null, empty, or whitespace.");
        }

        if (content.Length > 128)
        {
            throw new XyoClientException(HttpStatusCode.BadRequest, $"Transaction content exceeds maximum length of 128 characters (provided {content.Length} chars).");
        }

        if (string.IsNullOrWhiteSpace(countryCode))
        {
            throw new XyoClientException(HttpStatusCode.BadRequest, "Country code cannot be null, empty, or whitespace.");
        }

        string trimmed = countryCode.Trim();
        if (!CountryCodeRegex.IsMatch(trimmed))
        {
            throw new XyoClientException(HttpStatusCode.BadRequest, $"Invalid country code '{countryCode}'. Must be a 2-letter ISO 3166-1 alpha-2 country code.");
        }

        normalizedCountryCode = trimmed.ToUpperInvariant();
    }

    private static void ValidateApiUser(string? apiUser)
    {
        if (string.IsNullOrWhiteSpace(apiUser))
        {
            return;
        }

        if (CrlfRegex.IsMatch(apiUser))
        {
            throw new XyoClientException(HttpStatusCode.BadRequest, "Tenant user identifier contains forbidden CRLF injection characters (CWE-113).");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(XyoClient));
        }
    }

    /// <summary>
    /// Disposes the underlying HttpClient if owned by this client.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
