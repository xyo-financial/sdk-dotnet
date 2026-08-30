using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
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
using Xyo.Sdk.Internal;
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
        XyoClientConfig.ValidateEffectiveBaseUrl(_config.BaseUrl, "XyoClientConfig.BaseUrl", nameof(config));

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

        // Only materialises a copy on the first divergence between the input and its normalised form (e.g.
        // a lowercase country code). For the common case where every item is already normalised, this avoids
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
            if (!string.Equals(normalized, item.CountryCode, StringComparison.Ordinal))
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

        // The specification declares GET /v1/ai/finance/enrichment/status/{id}, with the work
        // identifier as a path parameter rather than a query value. EscapeDataString is the
        // correct escape for a path segment: unlike a query value it also escapes '/', so an
        // identifier containing a slash cannot inject additional path segments.
        var statusUri = new Uri($"{_config.BaseUrl}/v1/ai/finance/enrichment/status/{Uri.EscapeDataString(id.Trim())}");

        var httpRequest = new HttpRequestMessage(HttpMethod.Get, statusUri);
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

            using var responseStream = await response!.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            // DownloadTimeout is the per-read idle bound (it trips only when the peer stops sending);
            // MaxTotalDownloadDuration bounds the cumulative time spent waiting on the network, so a peer
            // that drips bytes just inside every idle window cannot hold the transfer open indefinitely.
            // Neither counts the caller's own processing time between yielded records. leaveOpen because the
            // `using` on `responseStream` above already owns its lifetime.
            using var idleStream = new IdleTimeoutStream(
                responseStream,
                _config.DownloadTimeout,
                _config.MaxTotalDownloadDuration,
                leaveOpen: true);

            await foreach (var item in TarStreamReader.StreamArchiveAsync(
                idleStream,
                _config.MaxArchiveBytes,
                _config.MaxDecompressedBytes,
                _config.MaxEntryBytes,
                _config.MaxTarEntries,
                cancellationToken).ConfigureAwait(false))
            {
                yield return item;
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

    private const int MaxUnaryResponseChars = 1_048_576; // 1 MiB, ~8x the largest plausible batch receipt

    /// <summary>
    /// How much of an oversized response body is kept as a diagnostic prefix on the thrown exception.
    /// </summary>
    private const int OversizeDiagnosticChars = 512;

    /// <summary>
    /// Deserializes a response body, translating malformed-payload failures (e.g. a required field the
    /// server sent as null) into a typed <see cref="XyoServerException"/> instead of letting a raw
    /// <see cref="System.Text.Json.JsonException"/> or <see cref="ArgumentException"/> escape.
    /// </summary>
    private static async Task<T> DeserializeResponseAsync<T>(Stream stream, HttpStatusCode statusCode, string emptyPayloadMessage, CancellationToken cancellationToken)
    {
        // Buffered with an upper bound rather than parsed directly off the stream so a schema-mismatch failure
        // can still attach the payload that caused it without risking an unbounded memory allocation (OOM)
        // on malformed or unexpected oversized responses (e.g. gateway HTML error pages).
        char[] buffer = ArrayPool<char>.Shared.Rent(MaxUnaryResponseChars);
        string raw;
        int totalRead = 0;
        bool readCompleted = false;
        try
        {
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);
            totalRead = await reader.ReadBlockAsync(buffer.AsMemory(0, MaxUnaryResponseChars), cancellationToken).ConfigureAwait(false);

            // Verify there is no excess content past the maximum allowed unary response size
            if (totalRead == MaxUnaryResponseChars)
            {
                var oneChar = new char[1];
                if (await reader.ReadAsync(oneChar.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) > 0)
                {
                    // Carry a bounded, log-safe prefix so the failure is diagnosable: whether this was a
                    // gateway HTML error page, a truncated proxy response, or a genuinely oversized payload
                    // is otherwise unanswerable without reproducing the call under a packet capture.
                    string prefix = LogSafeText.FlattenControlCharacters(new string(buffer, 0, Math.Min(totalRead, OversizeDiagnosticChars)));
                    throw new XyoServerException(statusCode,
                        $"API response exceeded the maximum supported size of {MaxUnaryResponseChars} characters.",
                        rawResponseBody: prefix);
                }
            }

            raw = new string(buffer, 0, totalRead);
            readCompleted = true;
        }
        finally
        {
            // Scrub only the region actually written, rather than asking the pool to zero the whole 1 MiB
            // rental. A typical enrichment response is a few hundred characters, so `clearArray: true` was a
            // 2 MB memset on every unary call (~15.7us measured) to erase bytes nobody wrote. On a faulted
            // read the written extent is unknown, so the full rental is scrubbed: response data must never
            // be handed back to a process-wide pool.
            Array.Clear(buffer, 0, readCompleted ? totalRead : buffer.Length);
            ArrayPool<char>.Shared.Return(buffer, clearArray: false);
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

    // HttpRequestMessage instances built by the unary methods above are deliberately NOT wrapped in `using`.
    // HttpClient does not dispose the request or its content (verified), so an analyser such as CA2000 will
    // flag this, but disposing it here buys nothing and costs something real:
    //
    //   * There is no unmanaged resource to release. JsonContent and StringContent hold managed memory with
    //     no handles and no finalizer, so the GC reclaims them either way.
    //   * Disposing the request disposes its Content, which breaks deferred inspection by a caller-supplied
    //     DelegatingHandler. AddXyoClient returns an IHttpClientBuilder precisely so consumers can install
    //     logging, audit, or retry handlers; one that captures a request to inspect after the pipeline
    //     completes would get ObjectDisposedException on its Content.
    //   * It bakes in single-use semantics. A disposed HttpRequestMessage cannot be resent, and .NET 5
    //     relaxed request reuse specifically to allow SDK-side retry, which is not implemented yet.
    //
    // Revisit only if a future request body holds an unmanaged resource (e.g. a StreamContent over a file).
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

        return $"[HTTP {statusCode}] {LogSafeText.Summarize(rawPayload)}";
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

    /// <summary>
    /// Stream wrapper enforcing two independent bounds on an archive transfer, neither of which counts time
    /// the caller spends processing yielded records between reads: an idle stall timeout, reset on every
    /// read, which trips only when the peer stops sending; and a cumulative budget on total time spent
    /// waiting on the network, which bounds the transfer as a whole even when every individual read
    /// completes inside the idle window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second bound exists because the first one alone bounds nothing cumulative: a peer that delivers a
    /// few bytes just inside every idle window keeps the connection, the socket, and the enumerating task
    /// alive indefinitely, since no single read ever stalls.
    /// </para>
    /// <para>
    /// Asynchronous reads only. <see cref="Stream.Read(byte[], int, int)"/> and
    /// <see cref="Stream.Read(Span{byte})"/> accept no <see cref="CancellationToken"/>, so neither bound can
    /// be enforced on a synchronous read without abandoning a thread. They throw
    /// <see cref="NotSupportedException"/> rather than reading with no timeout at all behind an API that
    /// looks like it has one.
    /// </para>
    /// </remarks>
    internal sealed class IdleTimeoutStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _leaveOpen;
        private readonly TimeSpan _idleTimeout;
        private readonly TimeSpan _totalBudget;
        private long _cumulativeReadTicks;

        /// <param name="inner">The stream to read through.</param>
        /// <param name="idleTimeout">Maximum time a single read may wait before it is treated as a stall.</param>
        /// <param name="totalBudget">
        /// Maximum cumulative time that may be spent waiting on <paramref name="inner"/> across all reads.
        /// <see cref="Timeout.InfiniteTimeSpan"/> disables the bound.
        /// </param>
        /// <param name="leaveOpen">
        /// When true (the default) disposing this stream does not dispose <paramref name="inner"/>. The
        /// wrapper is handed a stream it does not own, matching <c>BoundedReadStream</c> and the
        /// <c>leaveOpen: true</c> convention used by the rest of the archive pipeline.
        /// </param>
        public IdleTimeoutStream(Stream inner, TimeSpan idleTimeout, TimeSpan totalBudget, bool leaveOpen = true)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _idleTimeout = idleTimeout;
            _totalBudget = totalBudget;
            _leaveOpen = leaveOpen;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(
            "IdleTimeoutStream is asynchronous-only: a synchronous read cannot be bounded by the idle or " +
            "total transfer timeout. Use ReadAsync.");

        public override int Read(Span<byte> buffer) => throw new NotSupportedException(
            "IdleTimeoutStream is asynchronous-only: a synchronous read cannot be bounded by the idle or " +
            "total transfer timeout. Use ReadAsync.");

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_idleTimeout);

            long start = Stopwatch.GetTimestamp();
            try
            {
                int read = await _inner.ReadAsync(buffer, cts.Token).ConfigureAwait(false);

                // Accumulated on the success path only. The stall path below throws regardless, and adding to
                // the budget from a finally block would let a budget violation replace the stall exception
                // already propagating out.
                _cumulativeReadTicks += Stopwatch.GetTimestamp() - start;
                ThrowIfTotalBudgetExceeded();
                return read;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new XyoNetworkException(
                    $"Archive download stalled for more than {_idleTimeout.TotalSeconds} seconds.", ex);
            }
        }

        private void ThrowIfTotalBudgetExceeded()
        {
            if (_totalBudget == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            TimeSpan spentOnNetwork = Stopwatch.GetElapsedTime(0, _cumulativeReadTicks);
            if (spentOnNetwork > _totalBudget)
            {
                throw new XyoNetworkException(
                    $"Archive download exceeded the maximum total network transfer time of " +
                    $"{_totalBudget.TotalSeconds} seconds (spent {spentOnNetwork.TotalSeconds:F1}s waiting on the peer). " +
                    "Raise XyoClientConfig.MaxTotalDownloadDuration if archives of this size are expected to take longer.");
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_leaveOpen)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
