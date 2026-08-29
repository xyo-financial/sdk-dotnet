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
    private static readonly Regex CountryCodeRegex = new(@"^[A-Za-z]{2}\z", RegexOptions.Compiled);
    private static readonly Regex TraceparentRegex = new(
        @"^[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}\z",
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
    private int _disposed; // 0 = not disposed, 1 = disposed; mutated only via Interlocked, see Dispose()

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

        // BaseUrl's ambient default (XYO_API_BASE_URL) is deliberately NOT validated at config
        // construction time, so a bad environment variable can never preempt an explicit BaseUrl
        // override -- see the comment on XyoClientConfig's `_baseUrl` field initializer. That means the
        // effective value must be validated here instead, the first point where we know for certain
        // whether an override was supplied.
        try
        {
            XyoClientConfig.NormalizeBaseUrl(_config.BaseUrl);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                $"XyoClientConfig.BaseUrl '{_config.BaseUrl}' is invalid: {ex.Message} " +
                "If BaseUrl was not set explicitly, check the XYO_API_BASE_URL environment variable.",
                nameof(config), ex);
        }

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

        // Only materializes a copy on the first divergence between the input and its normalized form (e.g.
        // a lowercase country code). For the common case where every item is already normalized, this avoids
        // doubling peak managed heap with a second List plus up to 50,000 fresh EnrichmentRequest instances.
        List<EnrichmentRequest>? effectiveList = null;
        for (int i = 0; i < requestList.Count; i++)
        {
            var item = requestList[i];
            if (item == null)
            {
                throw new ArgumentNullException(nameof(requests), $"Transaction item at index {i} cannot be null.");
            }
            ValidateTransactionInput(item.Content, item.CountryCode, out string normalized);
            if (!ReferenceEquals(normalized, item.CountryCode))
            {
                effectiveList ??= new List<EnrichmentRequest>(requestList);
                effectiveList[i] = new EnrichmentRequest(item.Content, normalized);
            }
        }
        IReadOnlyList<EnrichmentRequest> effective = effectiveList ?? requestList;

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

        httpRequest.Content = JsonContent.Create(effective, options: DefaultJsonOptions);

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
    internal const int MaxDownloadRedirects = 5;

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

                // Re-decided on every hop, since a redirect can move the target from an internal host to an
                // external one or vice versa. Neither the Bearer token, nor X-Correlation-ID/traceparent
                // (live trace/span IDs), nor DefaultHeaders (which may carry caller secrets like an
                // internal API key) are sent to external storage hosts.
                bool isExternalStorage = _securityPolicy.IsExternalStorageHost(validatedUri.Host);
                if (!isExternalStorage)
                {
                    string token = await _config.ResolveTokenAsync(effectiveToken).ConfigureAwait(false);
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                ApplyDefaultHeaders(httpRequest, includeInternalOnlyHeaders: !isExternalStorage);

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

                // hop is 0-indexed and this check runs before following the redirect just received, so
                // "hop >= MaxDownloadRedirects" (not "- 1") is what actually allows MaxDownloadRedirects
                // redirects to be followed before giving up on the next one.
                if (hop >= MaxDownloadRedirects)
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

            // From here on, DownloadTimeout is enforced per-record (reset before each one) instead of as a
            // single deadline spanning the whole enumeration. The timeoutCts above ticks continuously in
            // real time regardless of what the caller does between MoveNextAsync calls -- with a single
            // deadline for the entire await-foreach, time the CALLER spends processing each yielded record
            // (e.g. a database write) counted against the SDK's own budget. At the shipped defaults
            // (MaxTarEntries=50,000, DownloadTimeout=10 min) that budget is exhausted at ~12ms of consumer
            // work per record, on a network that was never slow. A fresh per-record window means only an
            // actual stall in the network read or decompression trips it; the caller's own processing time
            // is never counted, no matter how long it takes.
            var recordEnumerator = TarStreamReader.StreamArchiveAsync(
                responseStream,
                _config.MaxArchiveBytes,
                _config.MaxDecompressedBytes,
                _config.MaxEntryBytes,
                _config.MaxTarEntries,
                cancellationToken).GetAsyncEnumerator(cancellationToken);

            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await recordEnumerator.MoveNextAsync().AsTask()
                            .WaitAsync(_config.DownloadTimeout, cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException ex)
                    {
                        // Task.WaitAsync(TimeSpan, CancellationToken) throws TimeoutException when the
                        // per-record budget elapses, and OperationCanceledException (left unhandled, so it
                        // propagates as-is) when cancellationToken itself fires -- the two are already
                        // disambiguated for us, unlike the SendAsync phase above which has to distinguish
                        // them via the `when` filter on a single combined token.
                        throw new XyoNetworkException(
                            $"Archive download stalled for more than {_config.DownloadTimeout.TotalSeconds} seconds without producing a record.", ex);
                    }

                    if (!hasNext)
                    {
                        break;
                    }

                    yield return recordEnumerator.Current;
                }
            }
            finally
            {
                try
                {
                    await recordEnumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort cleanup. Disposing the inner tar/gzip reader chain mid-entry (e.g. after
                    // the timeout above, or the caller breaking out of the enumeration early) can itself
                    // throw while it tries to skip the unread remainder of a non-seekable stream -- an
                    // exception here would otherwise REPLACE whatever exception is already propagating out
                    // of the try block above per C#'s finally-block semantics, masking the real failure
                    // behind an unrelated one from cleanup.
                }
            }
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <param name="request">The outbound request to attach headers to.</param>
    /// <param name="correlationId">Per-call correlation ID override; falls back to the configured default.</param>
    /// <param name="traceparent">Per-call traceparent override; falls back to the configured default.</param>
    /// <param name="includeInternalOnlyHeaders">
    /// Whether to attach X-Correlation-ID, traceparent, and DefaultHeaders. False when the request targets
    /// an external archive storage host (see <see cref="DownloadSecurityPolicy.IsExternalStorageHost"/>):
    /// distributed-tracing headers carry live trace/span IDs and DefaultHeaders may carry caller secrets
    /// (e.g. an internal API key), neither of which should follow the request to a third party any more
    /// than the Bearer token does.
    /// </param>
    private void ApplyDefaultHeaders(HttpRequestMessage request, string? correlationId = null, string? traceparent = null, bool includeInternalOnlyHeaders = true)
    {
        if (!includeInternalOnlyHeaders)
        {
            return;
        }

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
        // Buffered rather than parsed directly off the stream so a schema-mismatch failure can still
        // attach the payload that caused it: JsonSerializer.DeserializeAsync(stream) consumes the stream
        // as it parses, so if a straight streaming parse failed there would be nothing left to read back
        // -- the one exception whose entire purpose is diagnosing a malformed payload would carry no
        // payload. Unary response bodies here are modest (a single record, a batch job receipt, a status
        // lookup), unlike the archive path, which stays genuinely streaming for exactly that reason.
        string raw;
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false))
        {
            raw = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        T? result;
        try
        {
            result = System.Text.Json.JsonSerializer.Deserialize<T>(raw, DefaultJsonOptions);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
            throw new XyoServerException(statusCode, "API returned a payload that does not conform to the enrichment schema.", rawResponseBody: raw, innerException: ex);
        }

        return result ?? throw new XyoServerException(statusCode, emptyPayloadMessage, rawResponseBody: raw);
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
                    ArrayPool<char>.Shared.Return(charBuffer, clearArray: true);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ignored: best-effort read of the error body for the exception message; a malformed or
                // truncated body still falls through to the fallback message below. Cancellation is not
                // swallowed here, so a caller cancelling mid-read still observes OperationCanceledException.
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
        string flattened = FlattenControlCharacters(rawPayload);
        string clamped = Truncate(flattened, maxLength);
        return $"[HTTP {statusCode}] {clamped}";
    }

    /// <summary>
    /// Replaces every control character -- not just CR/LF -- with a space. CR/LF alone (the original
    /// CWE-117 fix) leaves U+2028/U+2029 (line separators to most JS-based log viewers), ESC (ANSI escape
    /// injection into a terminal-rendered log), and NUL (message truncation in C-based sinks) all intact.
    /// <c>char.IsControl</c> already covers everything TarStreamReader.ValidateEntryName checks for; this
    /// adds the two Unicode line separators it does not.
    /// </summary>
    private static string FlattenControlCharacters(string raw) =>
        string.Create(raw.Length, raw, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                span[i] = (char.IsControl(c) || c == '\u2028' || c == '\u2029') ? ' ' : c;
            }
        });

    /// <summary>
    /// Truncates to at most <paramref name="maxLength"/> UTF-16 code units without splitting a surrogate
    /// pair, which a naive <c>value[..maxLength]</c> can do and which some structured-log serializers
    /// reject outright as invalid UTF-16.
    /// </summary>
    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        int cut = maxLength;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return string.Concat(value.AsSpan(0, cut), "…");
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
            if (int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out int result))
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

            if (int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    /// <summary>
    /// Disposes the underlying HttpClient if owned by this client.
    /// </summary>
    public void Dispose()
    {
        // Interlocked.Exchange makes the check-and-set atomic: two threads racing Dispose() against each
        // other, or against an in-flight request's ThrowIfDisposed check, can no longer observe a
        // partially-disposed state (the plain bool check-then-act this replaced had that gap).
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
        GC.SuppressFinalize(this);
    }
}
