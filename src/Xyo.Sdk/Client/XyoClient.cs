using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
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
public sealed class XyoClient : IXyoClient
{
    private static readonly Regex CrlfRegex = new(@"[\r\n]", RegexOptions.Compiled);
    private static readonly Regex CountryCodeRegex = new(@"^[A-Za-z]{2}$", RegexOptions.Compiled);
    private static readonly Regex TraceparentRegex = new(
        @"^[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly System.Text.Json.JsonSerializerOptions DefaultJsonOptions = CreateJsonSerializerOptions();

    /// <summary>
    /// The single <see cref="System.Text.Json.JsonSerializerOptions"/> instance used for all enrichment payload
    /// deserialization across the SDK (unary responses and streamed archive entries alike), so every code path
    /// agrees on converters and null handling.
    /// </summary>
    internal static System.Text.Json.JsonSerializerOptions SerializerOptions => DefaultJsonOptions;

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
    /// <remarks>
    /// When <paramref name="httpClient"/> is supplied by the caller (or via DI), the SDK does not own its
    /// handler and cannot force <c>AllowAutoRedirect = false</c> on it. Egress/SSRF validation on archive
    /// downloads (see <see cref="DownloadSecurityPolicy"/>) is only guaranteed complete when the SDK
    /// constructs its own <see cref="HttpClient"/> (i.e. <paramref name="httpClient"/> is <c>null</c>) or
    /// when the caller's handler also disables automatic redirects.
    /// </remarks>
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
                ConnectTimeout = TimeSpan.FromSeconds(10),
                // The SDK validates every redirect hop itself (see StreamEnrichmentCollectionAsync) against
                // the download allowlist; letting the handler auto-follow would bypass that validation (SSRF).
                AllowAutoRedirect = false
            };
            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                // HttpClient.Timeout is a single TOTAL deadline that keeps running while a response stream is
                // consumed, which would kill a multi-hundred-MB archive download mid-stream. Deadlines are
                // enforced per call instead, via linked CancellationTokenSources: Timeout for unary calls
                // (see SendRequestAsync) and DownloadTimeout for StreamEnrichmentCollectionAsync.
                Timeout = System.Threading.Timeout.InfiniteTimeSpan
            };
            _ownsHttpClient = true;
        }
    }

    /// <inheritdoc />
    public Task<EnrichmentResponse> EnrichTransactionAsync(string content, string countryCode, CancellationToken cancellationToken = default)
    {
        return EnrichTransactionAsync(content, countryCode, (string?)null, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<EnrichmentResponse> EnrichTransactionAsync(string content, string countryCode, Guid? correlationId, string? traceparent = null, CancellationToken cancellationToken = default)
    {
        return EnrichTransactionAsync(content, countryCode, correlationId?.ToString("D"), traceparent, cancellationToken);
    }

    /// <inheritdoc />
    public Task<EnrichmentResponse> EnrichTransactionAsync(string content, string countryCode, string? correlationId, string? traceparent = null, CancellationToken cancellationToken = default)
    {
        ValidateTransactionInput(content, countryCode, out string normalizedCountryCode);
        var request = new EnrichmentRequest(content: content, countryCode: normalizedCountryCode);
        return EnrichTransactionAsync(request, correlationId, traceparent, cancellationToken);
    }

    /// <inheritdoc />
    public Task<EnrichmentResponse> EnrichTransactionAsync(EnrichmentRequest request, CancellationToken cancellationToken = default)
    {
        return EnrichTransactionAsync(request, (string?)null, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<EnrichmentResponse> EnrichTransactionAsync(EnrichmentRequest request, Guid? correlationId, string? traceparent = null, CancellationToken cancellationToken = default)
    {
        return EnrichTransactionAsync(request, correlationId?.ToString("D"), traceparent, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EnrichmentResponse> EnrichTransactionAsync(EnrichmentRequest request, string? correlationId, string? traceparent = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ValidateTransactionInput(request.Content, request.CountryCode, out string normalizedCountryCode);
        var effectiveRequest = new EnrichmentRequest(request.Content, normalizedCountryCode);

        string token = await _config.ResolveTokenAsync(cancellationToken).ConfigureAwait(false);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/v1/ai/finance/enrichment/transaction");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        ApplyDefaultHeaders(httpRequest, correlationId, traceparent);

        httpRequest.Content = JsonContent.Create(effectiveRequest, options: DefaultJsonOptions);

        using var response = await SendRequestAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);

        var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await DeserializeResponseAsync<EnrichmentResponse>(responseStream, response.StatusCode, "API server returned an empty payload.", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<EnrichTransactionCollectionResponse> EnrichTransactionsAsync(
        IEnumerable<EnrichmentRequest> requests,
        string? apiUser = null,
        CancellationToken cancellationToken = default)
    {
        return EnrichTransactionsAsync(requests, apiUser, (string?)null, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<EnrichTransactionCollectionResponse> EnrichTransactionsAsync(
        IEnumerable<EnrichmentRequest> requests,
        string? apiUser,
        Guid? correlationId,
        string? traceparent = null,
        CancellationToken cancellationToken = default)
    {
        return EnrichTransactionsAsync(requests, apiUser, correlationId?.ToString("D"), traceparent, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EnrichTransactionCollectionResponse> EnrichTransactionsAsync(
        IEnumerable<EnrichmentRequest> requests,
        string? apiUser,
        string? correlationId,
        string? traceparent = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (requests == null)
        {
            throw new ArgumentNullException(nameof(requests));
        }

        var requestList = requests as IReadOnlyList<EnrichmentRequest> ?? requests.ToList();
        if (requestList.Count == 0)
        {
            throw new ArgumentException("Transaction collection batch cannot be empty. Must contain between 1 and 50,000 items.", nameof(requests));
        }
        if (requestList.Count > 50_000)
        {
            throw new ArgumentException($"Transaction collection batch size of {requestList.Count} exceeds maximum limit of 50,000 items.", nameof(requests));
        }

        var effectiveList = new List<EnrichmentRequest>(requestList.Count);
        for (int i = 0; i < requestList.Count; i++)
        {
            var item = requestList[i];
            if (item == null)
            {
                throw new ArgumentNullException(nameof(requests), $"Transaction item at index {i} cannot be null.");
            }
            ValidateTransactionInput(item.Content, item.CountryCode, out string normalized);
            effectiveList.Add(new EnrichmentRequest(item.Content, normalized));
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

        ApplyDefaultHeaders(httpRequest, correlationId, traceparent);

        httpRequest.Content = JsonContent.Create(effectiveList, options: DefaultJsonOptions);

        using var response = await SendRequestAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);

        var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await DeserializeResponseAsync<EnrichTransactionCollectionResponse>(responseStream, response.StatusCode, "API server returned an empty batch response.", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<EnrichmentCollectionStatusResponse> GetEnrichmentStatusAsync(
        string id,
        string? apiUser = null,
        CancellationToken cancellationToken = default)
    {
        return GetEnrichmentStatusAsync(id, apiUser, (string?)null, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<EnrichmentCollectionStatusResponse> GetEnrichmentStatusAsync(
        string id,
        string? apiUser,
        Guid? correlationId,
        string? traceparent = null,
        CancellationToken cancellationToken = default)
    {
        return GetEnrichmentStatusAsync(id, apiUser, correlationId?.ToString("D"), traceparent, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EnrichmentCollectionStatusResponse> GetEnrichmentStatusAsync(
        string id,
        string? apiUser,
        string? correlationId,
        string? traceparent = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Enrichment job identifier cannot be null, empty, or whitespace.", nameof(id));
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

        ApplyDefaultHeaders(httpRequest, correlationId, traceparent);

        using var response = await SendRequestAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);

        var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await DeserializeResponseAsync<EnrichmentCollectionStatusResponse>(responseStream, response.StatusCode, "API server returned an empty status response.", cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Maximum number of redirect hops <see cref="StreamEnrichmentCollectionAsync"/> will follow. Each hop's
    /// target is re-validated against the download allowlist before it is requested.
    /// </summary>
    private const int MaxDownloadRedirects = 5;

    /// <inheritdoc />
    public async IAsyncEnumerable<EnrichmentResponse> StreamEnrichmentCollectionAsync(
        string downloadUrl,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        Uri validatedUri = _securityPolicy.ValidateDownloadUrl(downloadUrl);
        HttpResponseMessage? response = null;

        // DownloadTimeout bounds the whole operation (every redirect hop plus the full download and
        // decompression), independently of the shorter unary-call Timeout -- see SendRequestAsync.
        using var timeoutCts = new CancellationTokenSource(_config.DownloadTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        CancellationToken effectiveToken = linkedCts.Token;

        try
        {
            for (int hop = 0; ; hop++)
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Get, validatedUri);
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/gzip"));
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-tar"));
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream", 0.9));
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));

                // Attach Authorization only if NOT an external S3 / storage host. Re-decided on every hop,
                // since a redirect can move the target from an internal host to an external one or vice versa.
                if (!_securityPolicy.IsExternalStorageHost(validatedUri.Host))
                {
                    string token = await _config.ResolveTokenAsync(effectiveToken).ConfigureAwait(false);
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                ApplyDefaultHeaders(httpRequest);

                try
                {
                    response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, effectiveToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new XyoNetworkException($"Archive download timed out after {_config.DownloadTimeout.TotalSeconds} seconds.", ex);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new XyoNetworkException($"Download transport failure: {ex.Message}", ex);
                }

                int statusCodeInt = (int)response.StatusCode;
                bool isRedirect = statusCodeInt is 301 or 302 or 303 or 307 or 308;
                Uri? redirectLocation = isRedirect ? response.Headers.Location : null;

                if (redirectLocation == null)
                {
                    break;
                }

                if (hop >= MaxDownloadRedirects - 1)
                {
                    throw new XyoClientException(System.Net.HttpStatusCode.BadRequest,
                        $"Archive download exceeded the maximum of {MaxDownloadRedirects} redirects.");
                }

                Uri nextUri = redirectLocation.IsAbsoluteUri ? redirectLocation : new Uri(validatedUri, redirectLocation);
                response.Dispose();
                response = null;

                // Re-run the full allowlist/scheme validation on the redirect target -- this is the control
                // that stops a trusted host's 3xx from silently sending the client anywhere else (SSRF).
                validatedUri = _securityPolicy.ValidateDownloadUrl(nextUri.ToString());
            }

            await EnsureSuccessResponseAsync(response!, effectiveToken).ConfigureAwait(false);

            using var responseStream = await response!.Content.ReadAsStreamAsync(effectiveToken).ConfigureAwait(false);

            await foreach (var record in TarStreamReader.StreamArchiveAsync(
                responseStream,
                _config.MaxArchiveBytes,
                _config.MaxDecompressedBytes,
                _config.MaxEntryBytes,
                _config.MaxTarEntries,
                effectiveToken).ConfigureAwait(false))
            {
                yield return record;
            }
        }
        finally
        {
            response?.Dispose();
        }
    }

    private void ApplyDefaultHeaders(HttpRequestMessage request, string? correlationId = null, string? traceparent = null)
    {
        string? effectiveCorrelationId = !string.IsNullOrWhiteSpace(correlationId) ? correlationId : _config.CorrelationId;
        if (!string.IsNullOrWhiteSpace(effectiveCorrelationId))
        {
            ValidateHeaderValue(effectiveCorrelationId, nameof(correlationId));
            if (!request.Headers.NonValidated.Contains("X-Correlation-ID"))
            {
                request.Headers.TryAddWithoutValidation("X-Correlation-ID", effectiveCorrelationId);
            }
        }

        string? effectiveTraceparent = !string.IsNullOrWhiteSpace(traceparent) ? traceparent : _config.Traceparent;
        if (!string.IsNullOrWhiteSpace(effectiveTraceparent))
        {
            ValidateHeaderValue(effectiveTraceparent, nameof(traceparent));
            if (!TraceparentRegex.IsMatch(effectiveTraceparent))
            {
                throw new ArgumentException(
                    "Header 'traceparent' does not conform to the W3C TraceContext format (version-traceid-parentid-flags).", nameof(traceparent));
            }
            if (!request.Headers.NonValidated.Contains("traceparent"))
            {
                request.Headers.TryAddWithoutValidation("traceparent", effectiveTraceparent);
            }
        }

        foreach (var (key, value) in _config.DefaultHeaders)
        {
            if (!request.Headers.NonValidated.Contains(key))
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }
    }

    /// <summary>
    /// Deserializes a response body, translating malformed-payload failures (e.g. a required field the
    /// server sent as null) into a typed <see cref="XyoServerException"/> instead of letting a raw
    /// <see cref="System.Text.Json.JsonException"/> or <see cref="ArgumentException"/> escape.
    /// </summary>
    private static async Task<T> DeserializeResponseAsync<T>(Stream stream, HttpStatusCode statusCode, string emptyPayloadMessage, CancellationToken cancellationToken)
    {
        T? result;
        try
        {
            result = await System.Text.Json.JsonSerializer.DeserializeAsync<T>(stream, DefaultJsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
            throw new XyoServerException(statusCode, "API returned a payload that does not conform to the enrichment schema.", innerException: ex);
        }

        return result ?? throw new XyoServerException(statusCode, emptyPayloadMessage);
    }

    private static void ValidateHeaderValue(string val, string paramName)
    {
        if (CrlfRegex.IsMatch(val))
        {
            throw new ArgumentException($"Header '{paramName}' contains forbidden CRLF injection characters (CWE-113).", paramName);
        }
    }

    private async Task<HttpResponseMessage> SendRequestAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_config.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            return await _httpClient.SendAsync(request, completionOption, linkedCts.Token).ConfigureAwait(false);
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
                char[] charBuffer = ArrayPool<char>.Shared.Rent(maxChars);
                try
                {
                    int totalCharsRead = await reader.ReadBlockAsync(charBuffer.AsMemory(0, maxChars), cancellationToken).ConfigureAwait(false);
                    rawPayload = new string(charBuffer, 0, totalCharsRead);
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(charBuffer);
                }
            }
            catch
            {
                // Ignored
            }
        }

        int statusCodeInt = (int)response.StatusCode;

        if (statusCodeInt == 429)
        {
            var (retryAfter, limit, remaining, reset) = ParseRateLimitHeaders(response);
            if (!string.IsNullOrWhiteSpace(rawPayload) && response.Content?.Headers?.ContentType?.MediaType?.Contains("json") == true)
            {
                var probEx = XyoProblemDetailsException.FromJson(response.StatusCode, rawPayload);
                throw new RateLimitException(
                    response.StatusCode,
                    probEx.Message,
                    retryAfter: retryAfter,
                    rateLimitLimit: limit,
                    rateLimitRemaining: remaining,
                    rateLimitReset: reset,
                    type: probEx.Type,
                    title: probEx.Title,
                    status: probEx.Status,
                    detail: probEx.Detail,
                    instance: probEx.Instance,
                    errors: probEx.Errors,
                    rawResponseBody: rawPayload);
            }

            string msg = SafeSummary(rawPayload, statusCodeInt, "Rate limit exceeded. Too many requests.");
            throw new RateLimitException(
                response.StatusCode,
                msg,
                retryAfter: retryAfter,
                rateLimitLimit: limit,
                rateLimitRemaining: remaining,
                rateLimitReset: reset,
                rawResponseBody: rawPayload);
        }

        if (statusCodeInt >= 500)
        {
            string msg = SafeSummary(rawPayload, statusCodeInt, "Upstream server error.");
            throw new XyoServerException(response.StatusCode, msg, rawPayload);
        }

        if (statusCodeInt >= 400)
        {
            if (!string.IsNullOrWhiteSpace(rawPayload) && response.Content?.Headers?.ContentType?.MediaType?.Contains("json") == true)
            {
                throw XyoProblemDetailsException.FromJson(response.StatusCode, rawPayload);
            }

            string msg = SafeSummary(rawPayload, statusCodeInt, "Client error.");
            throw new XyoClientException(response.StatusCode, msg, rawPayload);
        }

        throw new XyoClientException(response.StatusCode, $"[HTTP {statusCodeInt}] Unexpected HTTP response.", rawPayload);
    }

    /// <summary>
    /// Builds a log-safe exception message summary from a raw server response body: CRLF-flattened (CWE-117)
    /// and length-clamped, never the full payload. Callers needing full fidelity use <c>RawResponseBody</c>
    /// on the thrown exception, which is never truncated or altered.
    /// </summary>
    private static string SafeSummary(string rawPayload, int statusCode, string fallback)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return $"[HTTP {statusCode}] {fallback}";
        }

        const int maxLength = 512;
        string flattened = CrlfRegex.Replace(rawPayload, " ");
        string clamped = flattened.Length > maxLength ? $"{flattened[..maxLength]}…" : flattened;
        return $"[HTTP {statusCode}] {clamped}";
    }

    private static (int? retryAfter, int? limit, int? remaining, int? reset) ParseRateLimitHeaders(HttpResponseMessage response)
    {
        int? retryAfter = ParseHeaderIntOrDelta(response, "Retry-After");
        int? limit = ParseHeaderInt(response, "RateLimit-Limit") ?? ParseHeaderInt(response, "X-RateLimit-Limit");
        int? remaining = ParseHeaderInt(response, "RateLimit-Remaining") ?? ParseHeaderInt(response, "X-RateLimit-Remaining");
        int? reset = ParseHeaderInt(response, "RateLimit-Reset") ?? ParseHeaderInt(response, "X-RateLimit-Reset");

        return (retryAfter, limit, remaining, reset);
    }

    private static int? ParseHeaderInt(HttpResponseMessage response, string headerName)
    {
        IEnumerable<string>? values = null;
        if (response.Headers.TryGetValues(headerName, out values) ||
            (response.Content != null && response.Content.Headers.TryGetValues(headerName, out values)))
        {
            string? val = values?.FirstOrDefault();
            if (int.TryParse(val, out int result))
            {
                return result;
            }
        }
        return null;
    }

    private static int? ParseHeaderIntOrDelta(HttpResponseMessage response, string headerName)
    {
        IEnumerable<string>? values = null;
        if (response.Headers.TryGetValues(headerName, out values) ||
            (response.Content != null && response.Content.Headers.TryGetValues(headerName, out values)))
        {
            string? val = values?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(val)) return null;

            if (int.TryParse(val, out int seconds))
            {
                return seconds;
            }
            if (DateTimeOffset.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                // Known limitation: UtcNow is captured at parse time, not at the moment the caller
                // acts on the result, so the returned delta may be slightly larger than the actual
                // remaining wait time (positive drift).
                var delta = (date - DateTimeOffset.UtcNow).TotalSeconds;
                return delta > 0 ? (int)Math.Ceiling(delta) : 0;
            }
        }
        return null;
    }

    private static void ValidateTransactionInput(string content, string countryCode, out string normalizedCountryCode)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Transaction content cannot be null, empty, or whitespace.", nameof(content));
        }

        if (content.Length > 128)
        {
            throw new ArgumentException($"Transaction content exceeds maximum length of 128 characters (provided {content.Length} chars).", nameof(content));
        }

        if (string.IsNullOrWhiteSpace(countryCode))
        {
            throw new ArgumentException("Country code cannot be null, empty, or whitespace.", nameof(countryCode));
        }

        string trimmed = countryCode.Trim();
        if (!CountryCodeRegex.IsMatch(trimmed))
        {
            throw new ArgumentException($"Invalid country code '{countryCode}'. Must be a 2-letter ISO 3166-1 alpha-2 country code.", nameof(countryCode));
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
            throw new ArgumentException("Tenant user identifier contains forbidden CRLF injection characters (CWE-113).", nameof(apiUser));
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
