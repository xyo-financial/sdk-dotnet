using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xyo.Generated.Api;
using Xyo.Generated.Client;
using Xyo.Generated.Model;
using Xyo.Sdk.Exceptions;
using Xyo.Sdk.Internal;
using Xyo.Sdk.Security;
using Xyo.Sdk.Streaming;
using Xyo.Sdk.Telemetry;

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
    /// <summary>
    /// Immutable snapshot pairing a <see cref="XyoClientConfig"/> with the <see cref="DownloadSecurityPolicy"/>
    /// derived from it, so the two are always swapped together atomically on a configuration reload -- a
    /// <see cref="DownloadSecurityPolicy"/> built from a stale <c>BaseUrl</c>/<c>TrustedDownloadHosts</c> pair
    /// would validate archive downloads against the wrong allowlist.
    /// </summary>
    private sealed class ConfigState
    {
        public ConfigState(XyoClientConfig config)
        {
            Config = config;
            SecurityPolicy = new DownloadSecurityPolicy(config.BaseUrl, config.TrustedDownloadHosts);
        }

        public XyoClientConfig Config { get; }

        public DownloadSecurityPolicy SecurityPolicy { get; }
    }

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    // Built exactly once, in the constructor, from the XyoClientConfig supplied at construction time -- never
    // re-read from _state afterwards. If this were instead derived from the (reloadable) current state, a
    // config reload could swap out the very logger used to report that reload, and a *rejected* reload would
    // end up reporting through a logger built from the (rejected) candidate configuration rather than the one
    // still actually in effect. That staleness is deliberate. Never null: XyoClientConfig.LoggerFactory
    // defaults to NullLoggerFactory.Instance, so this is always a usable (possibly no-op) logger.
    private readonly ILogger<XyoClient> _logger;

    private readonly IDisposable? _optionsChangeSubscription;

    // Reference reassignment of a `volatile` field is atomic and immediately visible across threads, so a
    // reader always observes either the previous ConfigState or the fully-constructed replacement, never a
    // torn mix of an old Config with a new SecurityPolicy (or vice versa). See OnOptionsChanged.
    private volatile ConfigState _state;
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
    /// <para>
    /// When <paramref name="httpClient"/> is supplied by the caller (or via DI), the SDK does not own its
    /// handler and cannot force <c>AllowAutoRedirect = false</c> on it. Egress/SSRF validation on archive
    /// downloads (see <see cref="DownloadSecurityPolicy"/>) is only guaranteed complete when the SDK
    /// constructs its own <see cref="HttpClient"/> (i.e. <paramref name="httpClient"/> is <c>null</c>) or
    /// when the caller's handler also disables automatic redirects.
    /// </para>
    /// <para>
    /// <paramref name="config"/> is read once, here, and never re-read afterwards: an instance constructed
    /// through this constructor is fixed for its lifetime and does not observe later mutation of the
    /// <see cref="XyoClientConfig"/> instance passed in (it is a record, so callers cannot mutate it anyway)
    /// nor any configuration source. This is the only reload behaviour a hand-constructed
    /// <see cref="XyoClient"/> gets; the DI-registered client obtained via
    /// <see cref="Xyo.Sdk.Extensions.ServiceCollectionExtensions.AddXyoClient(Microsoft.Extensions.DependencyInjection.IServiceCollection,Action{XyoClientOptions})"/>
    /// additionally tracks <c>IOptionsMonitor&lt;XyoClientOptions&gt;</c> reloads -- see the internal
    /// constructor below.
    /// </para>
    /// </remarks>
    public XyoClient(XyoClientConfig config, HttpClient? httpClient = null)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        // BaseUrl's ambient default (XYO_API_BASE_URL) is deliberately NOT validated at config
        // construction time, so a bad environment variable can never preempt an explicit BaseUrl
        // override -- see the comment on XyoClientConfig's `_baseUrl` field initializer. That means the
        // effective value must be validated here instead, the first point where we know for certain
        // whether an override was supplied.
        XyoClientConfig.ValidateEffectiveBaseUrl(config.BaseUrl, "XyoClientConfig.BaseUrl", nameof(config));

        _state = new ConfigState(config);
        _logger = config.LoggerFactory.CreateLogger<XyoClient>();

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
                // (see SendRequestAsync) and DownloadConnectTimeout and ReadIdleTimeout for
                // StreamEnrichmentCollectionAsync.
                Timeout = System.Threading.Timeout.InfiniteTimeSpan
            };
            _ownsHttpClient = true;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XyoClient"/> class that tracks live
    /// <see cref="XyoClientOptions"/> reloads via <paramref name="optionsMonitor"/>. Used exclusively by
    /// <see cref="Xyo.Sdk.Extensions.ServiceCollectionExtensions.AddXyoClient(Microsoft.Extensions.DependencyInjection.IServiceCollection,Action{XyoClientOptions})"/>
    /// so the DI-registered singleton observes <c>appsettings.json</c> changes without ever being
    /// reconstructed (its lifetime, and the container's <see cref="IDisposable"/> tracking of it, stay
    /// singleton -- see EPIC-004 / US-DOTNET-004).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The current <see cref="XyoClientConfig"/> is cached in <see cref="_state"/> and rebuilt only when
    /// <paramref name="optionsMonitor"/> raises its change token, never per call, so every SDK call after the
    /// first pays only a single volatile field read on the hot path.
    /// </para>
    /// <para>
    /// A reload that fails <see cref="XyoClientOptions.ToConfig"/> validation (e.g. an invalid
    /// <c>BaseUrl</c>) is rejected in <see cref="OnOptionsChanged"/>: the client keeps serving requests
    /// against its last valid configuration, and the failure is logged via the <see cref="ILogger"/> built
    /// once at construction time (see <see cref="_logger"/>) rather than swallowed or allowed to fault the
    /// change-notification thread.
    /// </para>
    /// </remarks>
    internal XyoClient(IOptionsMonitor<XyoClientOptions> optionsMonitor, HttpClient httpClient, ILoggerFactory? containerLoggerFactory = null)
        : this(
            BuildInitialConfig(optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor)), containerLoggerFactory),
            RequireHttpClient(httpClient))
    {
        // optionsMonitor.CurrentValue is Get(Options.DefaultName): the client's identity is "the default
        // XyoClientOptions instance", and OnOptionsChanged filters every notification against that same
        // name -- see its remarks.
        _optionsChangeSubscription = optionsMonitor.OnChange(OnOptionsChanged);
    }

    /// <summary>
    /// Builds the <see cref="XyoClientConfig"/> the internal constructor seeds <see cref="_state"/> from,
    /// applying the DI container's <see cref="ILoggerFactory"/> as a fallback when the caller has not
    /// explicitly configured one on <see cref="XyoClientOptions"/> (see
    /// <see cref="XyoClientConfig.IsLoggerFactoryExplicit"/>). A caller who set
    /// <see cref="NullLoggerFactory.Instance"/> explicitly, to silence the SDK, is respected: the container's
    /// factory is applied only when nothing was set at all.
    /// </summary>
    private static XyoClientConfig BuildInitialConfig(IOptionsMonitor<XyoClientOptions> optionsMonitor, ILoggerFactory? containerLoggerFactory)
    {
        XyoClientConfig config = optionsMonitor.CurrentValue.ToConfig();
        if (!config.IsLoggerFactoryExplicit && containerLoggerFactory != null)
        {
            config = config with { LoggerFactory = containerLoggerFactory };
        }
        return config;
    }

    /// <summary>
    /// Guards the internal constructor's <c>httpClient</c> parameter, which forwards straight into the
    /// public constructor's <c>httpClient != null</c> branch. A null slipping through would silently flip
    /// <c>_ownsHttpClient</c> to <c>true</c> and construct a second, SDK-owned <see cref="HttpClient"/>,
    /// defeating the entire purpose of this constructor: reusing the caller's
    /// <see cref="System.Net.Http.IHttpClientFactory"/>-managed client.
    /// </summary>
    private static HttpClient RequireHttpClient(HttpClient httpClient) =>
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    /// <summary>
    /// Applies (or rejects) a live <see cref="XyoClientOptions"/> reload reported by
    /// <see cref="IOptionsMonitor{TOptions}.OnChange(Action{TOptions,string})"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IOptionsMonitor{TOptions}.OnChange"/> fires for <b>every</b> named <c>XyoClientOptions</c>
    /// instance registered anywhere in the container, not only the default-named one this client was seeded
    /// from (<c>optionsMonitor.CurrentValue</c>, i.e. <c>Get(Options.DefaultName)</c>). A notification for any
    /// other name is therefore ignored below: adopting it would silently swap this client's <c>BaseUrl</c>
    /// and Bearer token for an unrelated profile's the moment an application registers a second, differently
    /// named section (a sandbox profile, a second tenant, a health-check probe) that happens to reload first
    /// (US-DOTNET-004).
    /// </para>
    /// <para>
    /// Runs on whatever thread the options change-token infrastructure invokes it on (typically a thread
    /// pool thread reacting to a file-system watcher), so an unhandled exception here would fault that
    /// thread rather than any caller of the SDK. Every exception is therefore caught: a validation failure
    /// (<see cref="ArgumentException"/> from <see cref="XyoClientOptions.ToConfig"/>, or the missing-credential
    /// guard below) is the expected, documented outcome of a bad edit to <c>appsettings.json</c> and is
    /// logged at <see cref="LogLevel.Error"/>; anything else is logged at the same level as a defensive
    /// backstop. In both cases <see cref="_state"/> is left untouched, so in-flight and subsequent calls keep
    /// using the last configuration that validated successfully.
    /// </para>
    /// </remarks>
    private void OnOptionsChanged(XyoClientOptions options, string? name)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            // Raced Dispose(): ChangeTokenRegistration.Dispose() unregisters but does not synchronise with a
            // callback already in flight. This client will never serve another request with this state, so
            // there is nothing useful left to do with a notification that arrives after disposal.
            return;
        }

        // A null name is IOptionsMonitor's own normalisation of the default name to Options.DefaultName
        // (string.Empty), so both must be treated as "the default instance" here.
        if (!string.Equals(name ?? Options.DefaultName, Options.DefaultName, StringComparison.Ordinal))
        {
            return;
        }

        XyoClientConfig newConfig;
        try
        {
            newConfig = options.ToConfig();

            // A reload is only ever supposed to be an improvement on a configuration that already worked.
            // ToConfig() validates BaseUrl and DefaultHeaders but has no way to know this is a *reload*
            // rather than a first-time construction, where deferring the credential (e.g. to be supplied
            // later via ApiKeySupplier) is legitimate. On the reload path it is not: accepting a config that
            // has lost its credential would take a healthy singleton to 100% failure with no way back short
            // of a restart, contradicting the "last valid configuration" guarantee this handler exists to
            // provide. So it is rejected here, on the same terms as an invalid BaseUrl.
            if (string.IsNullOrWhiteSpace(newConfig.ApiKey) && newConfig.ApiKeySupplier is null)
            {
                throw new ArgumentException(
                    "Reloaded XyoClientOptions supplies neither ApiKey nor ApiKeySupplier; the reload was "
                    + "rejected rather than leaving the client unable to authenticate.",
                    nameof(XyoClientOptions.ApiKey));
            }

            _state = new ConfigState(newConfig);
        }
        catch (Exception ex)
        {
            // ex.Message can carry operator-controlled text verbatim (e.g. a candidate BaseUrl, which may
            // itself have carried a credential before XyoClientConfig.NormalizeBaseUrl redacted that specific
            // case) as well as embedded control characters from a malformed appsettings.json value.
            // LogSafeText.Summarize flattens CR/LF and other log-line-forgery characters (CWE-117) and clamps
            // the length before this reaches the logging pipeline; the full exception is still attached via
            // the `ex` argument for a sink that can handle it safely.
            _logger.LogError(ex,
                "XyoClientOptions reload was rejected: {Reason}. XyoClient continues serving requests with its last valid configuration.",
                LogSafeText.Summarize(ex.Message));

            var handlers = OptionsReloadFailed;
            if (handlers != null)
            {
                foreach (EventHandler<Exception> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(this, ex);
                    }
                    catch (Exception handlerEx)
                    {
                        // A subscriber's exception must not propagate out of this handler: it would fault the
                        // options change-token infrastructure's thread, the exact failure mode this whole method
                        // exists to prevent for the reload itself.
                        _logger.LogError(handlerEx, "An OptionsReloadFailed handler threw; ignored.");
                    }
                }
            }
            return;
        }

        _logger.LogInformation(
            "XyoClientOptions reload applied: BaseUrl={BaseUrl}, TrustedDownloadHosts={TrustedHostCount}, Timeout={TimeoutSeconds}s, DownloadConnectTimeout={DownloadConnectTimeoutSeconds}s, ReadIdleTimeout={ReadIdleTimeoutSeconds}s.",
            newConfig.BaseUrl, newConfig.TrustedDownloadHosts.Count, newConfig.Timeout.TotalSeconds, newConfig.DownloadConnectTimeout.TotalSeconds, newConfig.ReadIdleTimeout.TotalSeconds);
    }

    /// <summary>
    /// Raised when an <see cref="IOptionsMonitor{TOptions}"/> reload is rejected by validation. Not part of
    /// the public API surface: production observability goes through the <see cref="ILogger"/> built once at
    /// construction time (see <see cref="_logger"/>), and this event exists solely so tests can assert
    /// deterministically that a rejected reload is surfaced rather than silently swallowed.
    /// </summary>
    internal event EventHandler<Exception>? OptionsReloadFailed;

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
        const string operationName = "EnrichTransaction";
        ThrowIfDisposed();
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ConfigState state = _state;
        long startTimestamp = Stopwatch.GetTimestamp();
        string? effectiveTraceparent = !string.IsNullOrWhiteSpace(traceparent) ? traceparent : state.Config.Traceparent;
        using Activity? activity = StartClientActivity(operationName, effectiveTraceparent);

        try
        {
            ValidateTransactionInput(request.Content, request.CountryCode, out string normalizedCountryCode);
            var effectiveRequest = new EnrichmentRequest(request.Content, normalizedCountryCode);

            string token = await state.Config.ResolveTokenAsync(cancellationToken).ConfigureAwait(false);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{state.Config.BaseUrl}/v1/ai/finance/enrichment/transaction");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            activity?.SetTag("http.request.method", "POST");
            activity?.SetTag("server.address", httpRequest.RequestUri!.Host);

            ApplyDefaultHeaders(httpRequest, state.Config, correlationId, traceparent, activity: activity);

            httpRequest.Content = JsonContent.Create(effectiveRequest, options: DefaultJsonOptions);

            using var response = await SendRequestAsync(httpRequest, HttpCompletionOption.ResponseContentRead, state.Config, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("http.response.status_code", XyoTelemetry.GetBoxedStatusCode(response.StatusCode));
            await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);

            var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await DeserializeResponseAsync<EnrichmentResponse>(responseStream, response.StatusCode, "API server returned an empty payload.", cancellationToken).ConfigureAwait(false);

            CompleteActivityStatus(activity, exception: null);
            RecordRequestMetrics(operationName, startTimestamp, exception: null);
            return result;
        }
        catch (Exception ex)
        {
            CompleteActivityStatus(activity, ex);
            RecordRequestMetrics(operationName, startTimestamp, ex);
            LogOperationFailure(operationName, ex);
            throw;
        }
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
        const string operationName = "EnrichTransactions";
        ThrowIfDisposed();
        if (requests == null)
        {
            throw new ArgumentNullException(nameof(requests));
        }

        ConfigState state = _state;
        long startTimestamp = Stopwatch.GetTimestamp();
        string? effectiveTraceparent = !string.IsNullOrWhiteSpace(traceparent) ? traceparent : state.Config.Traceparent;
        using Activity? activity = StartClientActivity(operationName, effectiveTraceparent);

        try
        {
            var requestList = requests as IReadOnlyList<EnrichmentRequest> ?? requests.ToList();
            if (requestList.Count == 0)
            {
                throw new ArgumentException("Transaction collection batch cannot be empty. Must contain between 1 and 50,000 items.", nameof(requests));
            }
            if (requestList.Count > 50_000)
            {
                throw new ArgumentException($"Transaction collection batch size of {requestList.Count} exceeds maximum limit of 50,000 items.", nameof(requests));
            }

            activity?.SetTag("xyo.sdk.batch.size", requestList.Count);

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

            string token = await state.Config.ResolveTokenAsync(cancellationToken).ConfigureAwait(false);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{state.Config.BaseUrl}/v1/ai/finance/enrichment/transactions");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (!string.IsNullOrWhiteSpace(apiUser))
            {
                httpRequest.Headers.Add("x-api-user", apiUser.Trim());
            }

            activity?.SetTag("http.request.method", "POST");
            activity?.SetTag("server.address", httpRequest.RequestUri!.Host);

            ApplyDefaultHeaders(httpRequest, state.Config, correlationId, traceparent, activity: activity);

            httpRequest.Content = JsonContent.Create(effective, options: DefaultJsonOptions);

            using var response = await SendRequestAsync(httpRequest, HttpCompletionOption.ResponseContentRead, state.Config, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("http.response.status_code", XyoTelemetry.GetBoxedStatusCode(response.StatusCode));
            await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);

            var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await DeserializeResponseAsync<EnrichTransactionCollectionResponse>(responseStream, response.StatusCode, "API server returned an empty batch response.", cancellationToken).ConfigureAwait(false);

            CompleteActivityStatus(activity, exception: null);
            RecordRequestMetrics(operationName, startTimestamp, exception: null);
            return result;
        }
        catch (Exception ex)
        {
            CompleteActivityStatus(activity, ex);
            RecordRequestMetrics(operationName, startTimestamp, ex);
            LogOperationFailure(operationName, ex);
            throw;
        }
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
        const string operationName = "GetEnrichmentStatus";
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Enrichment job identifier cannot be null, empty, or whitespace.", nameof(id));
        }

        ConfigState state = _state;
        long startTimestamp = Stopwatch.GetTimestamp();
        string? effectiveTraceparent = !string.IsNullOrWhiteSpace(traceparent) ? traceparent : state.Config.Traceparent;
        using Activity? activity = StartClientActivity(operationName, effectiveTraceparent);

        try
        {
            ValidateApiUser(apiUser);

            string token = await state.Config.ResolveTokenAsync(cancellationToken).ConfigureAwait(false);

            // The specification declares GET /v1/ai/finance/enrichment/status/{id}, with the work
            // identifier as a path parameter rather than a query value. EscapeDataString is the
            // correct escape for a path segment: unlike a query value it also escapes '/', so an
            // identifier containing a slash cannot inject additional path segments.
            var statusUri = new Uri($"{state.Config.BaseUrl}/v1/ai/finance/enrichment/status/{Uri.EscapeDataString(id.Trim())}");

            var httpRequest = new HttpRequestMessage(HttpMethod.Get, statusUri);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (!string.IsNullOrWhiteSpace(apiUser))
            {
                httpRequest.Headers.Add("x-api-user", apiUser.Trim());
            }

            activity?.SetTag("http.request.method", "GET");
            activity?.SetTag("server.address", statusUri.Host);

            ApplyDefaultHeaders(httpRequest, state.Config, correlationId, traceparent, activity: activity);

            using var response = await SendRequestAsync(httpRequest, HttpCompletionOption.ResponseContentRead, state.Config, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("http.response.status_code", XyoTelemetry.GetBoxedStatusCode(response.StatusCode));
            await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);

            var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await DeserializeResponseAsync<EnrichmentCollectionStatusResponse>(responseStream, response.StatusCode, "API server returned an empty status response.", cancellationToken).ConfigureAwait(false);

            CompleteActivityStatus(activity, exception: null);
            RecordRequestMetrics(operationName, startTimestamp, exception: null);
            return result;
        }
        catch (Exception ex)
        {
            CompleteActivityStatus(activity, ex);
            RecordRequestMetrics(operationName, startTimestamp, ex);
            LogOperationFailure(operationName, ex);
            throw;
        }
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

    /// <summary>
    /// Mutable, single-writer progress recorded by <see cref="StreamEnrichmentCollectionCoreAsync"/> while it
    /// runs, so the public <see cref="StreamEnrichmentCollectionAsync"/> wrapper can attach redirect-hop-count
    /// and bytes-transferred telemetry to the operation's span, including on a failure partway through (e.g. a
    /// stalled download reports the bytes read before it stalled).
    /// </summary>
    private sealed class StreamProgress
    {
        public int RedirectHopCount;
        public IdleTimeoutStream? IdleStream;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately split into this thin, non-yielding wrapper and the <see cref="StreamEnrichmentCollectionCoreAsync"/>
    /// iterator that does the actual work: a <c>yield return</c> is not allowed inside a <c>try</c> block that
    /// has a <c>catch</c> clause, so recording span status and metrics on failure (which needs a <c>catch</c>)
    /// has to live in a method that does not itself yield across that boundary. Manual enumeration here keeps
    /// the `yield return` outside any try/catch while still observing every exception the core method raises.
    /// </remarks>
    public async IAsyncEnumerable<EnrichmentResponse> StreamEnrichmentCollectionAsync(
        string downloadUrl,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const string operationName = "StreamEnrichmentCollection";
        ThrowIfDisposed();

        ConfigState state = _state;
        long startTimestamp = Stopwatch.GetTimestamp();
        using Activity? activity = StartClientActivity(operationName, state.Config.Traceparent);
        var statistics = new ArchiveTransferStatistics();
        var progress = new StreamProgress();
        Exception? failure = null;
        bool completed = false;

        try
        {
            await using var enumerator = StreamEnrichmentCollectionCoreAsync(downloadUrl, state, activity, statistics, progress, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                EnrichmentResponse current;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }
                    current = enumerator.Current;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    throw;
                }

                yield return current;
            }

            completed = true;
        }
        finally
        {
            // `completed == false` with `failure == null` means the consumer abandoned the enumeration
            // (break, .Take(n), .FirstOrDefaultAsync(), an exception in the consumer's own loop body): the
            // compiler-generated DisposeAsync unwinds the `await using` above without ever reaching the end
            // of the `try`. That is a real, distinct outcome, not a missing measurement -- a request counter
            // that skips a whole class of completions makes every dashboard built on it under-count exactly
            // the traffic pattern a streaming API invites, and the span would otherwise be left `Unset`
            // rather than classified either way.
            bool abandoned = !completed && failure is null;
            CompleteStreamActivity(activity, failure, progress, statistics, abandoned);
            RecordRequestMetrics(operationName, startTimestamp, failure, outcomeOverride: abandoned ? "abandoned" : null);
            if (failure is not null)
            {
                LogOperationFailure(operationName, failure);
            }
        }
    }

    private async IAsyncEnumerable<EnrichmentResponse> StreamEnrichmentCollectionCoreAsync(
        string downloadUrl,
        ConfigState state,
        Activity? activity,
        ArchiveTransferStatistics statistics,
        StreamProgress progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Uri validatedUri = state.SecurityPolicy.ValidateDownloadUrl(downloadUrl);
        HttpResponseMessage? response = null;

        activity?.SetTag("http.request.method", "GET");
        activity?.SetTag("server.address", validatedUri.Host);

        // DownloadConnectTimeout bounds only the connection/redirect phase (every redirect hop up to and
        // including receiving response headers), independently of both the shorter unary-call Timeout (see
        // SendRequestAsync) and the per-read idle bound applied to the archive body below (ReadIdleTimeout).
        using var timeoutCts = new CancellationTokenSource(state.Config.DownloadConnectTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        CancellationToken effectiveToken = linkedCts.Token;

        try
        {
            for (int hop = 0; ; hop++)
            {
                progress.RedirectHopCount = hop;
                var httpRequest = new HttpRequestMessage(HttpMethod.Get, validatedUri);
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/gzip"));
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-tar"));
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream", 0.9));
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));

                // Re-decided on every hop, since a redirect can move the target from an internal host to an
                // external one or vice versa. Neither the Bearer token, nor X-Correlation-ID/traceparent
                // (live trace/span IDs), nor DefaultHeaders (which may carry caller secrets like an
                // internal API key) are sent to external storage hosts.
                bool isExternalStorage = state.SecurityPolicy.IsExternalStorageHost(validatedUri.Host);
                if (!isExternalStorage)
                {
                    string token = await state.Config.ResolveTokenAsync(effectiveToken).ConfigureAwait(false);
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                ApplyDefaultHeaders(httpRequest, state.Config, includeInternalOnlyHeaders: !isExternalStorage, activity: activity);

                try
                {
                    response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, effectiveToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new XyoNetworkException(
                        $"Archive download connection phase timed out after {state.Config.DownloadConnectTimeout.TotalSeconds} " +
                        "seconds while establishing the connection, following redirects, or waiting for response headers.", ex);
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
                    if (XyoTelemetry.DownloadBoundTrippedCount.Enabled)
                    {
                        XyoTelemetry.DownloadBoundTrippedCount.Add(1, new KeyValuePair<string, object?>(XyoTelemetry.BoundTagKey, XyoTelemetry.BoundMaxRedirects));
                    }
                    throw new XyoClientException(System.Net.HttpStatusCode.BadRequest,
                        $"Archive download exceeded the maximum of {MaxDownloadRedirects} redirects.");
                }

                Uri nextUri = redirectLocation.IsAbsoluteUri ? redirectLocation : new Uri(validatedUri, redirectLocation);
                response.Dispose();
                response = null;

                // Re-run the full allowlist/scheme validation on the redirect target -- this is the control
                // that stops a trusted host's 3xx from silently sending the client anywhere else (SSRF).
                try
                {
                    validatedUri = state.SecurityPolicy.ValidateDownloadUrl(nextUri.ToString());
                }
                catch (XyoClientException ex)
                {
                    if (XyoTelemetry.RedirectRefusedCount.Enabled)
                    {
                        XyoTelemetry.RedirectRefusedCount.Add(1);
                    }
                    _logger.LogWarning(ex, "Archive download redirect was refused by the egress allowlist.");
                    throw;
                }
            }

            if (progress.RedirectHopCount > 0)
            {
                activity?.SetTag("http.request.resend_count", progress.RedirectHopCount);
            }

            try
            {
                activity?.SetTag("http.response.status_code", XyoTelemetry.GetBoxedStatusCode(response!.StatusCode));
                await EnsureSuccessResponseAsync(response!, effectiveToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new XyoNetworkException(
                    $"Archive download connection phase timed out after {state.Config.DownloadConnectTimeout.TotalSeconds} " +
                    "seconds while establishing the connection, following redirects, or waiting for response headers.", ex);
            }

            // The connection phase is over: DownloadConnectTimeout is spent and must not reach the body.
            // Disarming reuses the existing timer (Timer.Change under the hood) rather than leaving it
            // scheduled to fire mid-transfer for nothing, and turns effectiveToken from "in scope but must
            // not be used" into "in scope and inert" for the remainder of this method.
            timeoutCts.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);

            using var responseStream = await response!.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            // ReadIdleTimeout is the per-read idle bound (it trips only when the peer stops sending);
            // MaxTotalDownloadDuration bounds the cumulative time spent waiting on the network, so a peer
            // that drips bytes just inside every idle window cannot hold the transfer open indefinitely.
            // Neither counts the caller's own processing time between yielded records. leaveOpen because the
            // `using` on `responseStream` above already owns its lifetime.
            var idleStream = new IdleTimeoutStream(
                responseStream,
                state.Config.ReadIdleTimeout,
                state.Config.MaxTotalDownloadDuration,
                leaveOpen: true);
            progress.IdleStream = idleStream;

            try
            {
                await foreach (var item in TarStreamReader.StreamArchiveAsync(
                    idleStream,
                    state.Config.MaxArchiveBytes,
                    state.Config.MaxDecompressedBytes,
                    state.Config.MaxEntryBytes,
                    state.Config.MaxTarEntries,
                    statistics,
                    cancellationToken).ConfigureAwait(false))
                {
                    yield return item;
                }
            }
            finally
            {
                idleStream.Dispose();
            }
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <param name="request">The outbound request to attach headers to.</param>
    /// <param name="config">
    /// The <see cref="XyoClientConfig"/> snapshot the calling method captured at the start of the operation
    /// (see <see cref="ConfigState"/>), so a config reload mid-call cannot mix its default headers with a
    /// BaseUrl or timeout read from a different snapshot.
    /// </param>
    /// <param name="correlationId">Per-call correlation ID override; falls back to the configured default.</param>
    /// <param name="traceparent">Per-call traceparent override; falls back to the configured default.</param>
    /// <param name="includeInternalOnlyHeaders">
    /// Whether to attach X-Correlation-ID, traceparent, tracestate, and DefaultHeaders. False when the
    /// request targets an external archive storage host (see
    /// <see cref="DownloadSecurityPolicy.IsExternalStorageHost"/>): distributed-tracing headers carry live
    /// trace/span IDs and DefaultHeaders may carry caller secrets (e.g. an internal API key), neither of
    /// which should follow the request to a third party any more than the Bearer token does.
    /// </param>
    /// <param name="activity">
    /// The <see cref="Activity"/> started for this call, if any (see <see cref="StartClientActivity"/>).
    /// Used to resolve the outbound traceparent -- see <see cref="ResolveOutboundTraceparent"/> -- and to
    /// forward <see cref="Activity.TraceStateString"/> as <c>tracestate</c> when present.
    /// </param>
    private static void ApplyDefaultHeaders(HttpRequestMessage request, XyoClientConfig config, string? correlationId = null, string? traceparent = null, bool includeInternalOnlyHeaders = true, Activity? activity = null)
    {
        if (!includeInternalOnlyHeaders)
        {
            return;
        }

        string? effectiveCorrelationId = !string.IsNullOrWhiteSpace(correlationId) ? correlationId : config.CorrelationId;
        if (!string.IsNullOrWhiteSpace(effectiveCorrelationId))
        {
            ValidateHeaderValue(effectiveCorrelationId, nameof(correlationId));
            if (!request.Headers.NonValidated.Contains("X-Correlation-ID"))
            {
                request.Headers.TryAddWithoutValidation("X-Correlation-ID", effectiveCorrelationId);
            }
        }

        string? headerValue = ResolveOutboundTraceparent(activity, traceparent, config);
        if (headerValue is not null && !request.Headers.NonValidated.Contains("traceparent"))
        {
            request.Headers.TryAddWithoutValidation("traceparent", headerValue);
        }

        // tracestate carries vendor-specific sampling/state decisions alongside traceparent; only ever
        // sourced from the SDK's own started activity, never from a raw caller-supplied string, so there is
        // no separate format to validate here beyond the shared CRLF check.
        string? tracestate = activity?.TraceStateString;
        if (!string.IsNullOrWhiteSpace(tracestate) && !request.Headers.NonValidated.Contains("tracestate"))
        {
            ValidateHeaderValue(tracestate, "tracestate");
            request.Headers.TryAddWithoutValidation("tracestate", tracestate);
        }

        foreach (var (key, value) in config.DefaultHeaders)
        {
            if (!request.Headers.NonValidated.Contains(key))
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }
    }

    /// <summary>
    /// Resolves the traceparent value actually placed on the wire.
    /// </summary>
    /// <remarks>
    /// Any caller-supplied or configured traceparent is always validated (CRLF and W3C format) when
    /// present, regardless of whether it ends up being what is sent. The value actually returned then
    /// prefers the SDK's own client span, whenever <paramref name="activity"/> was started and produced a
    /// W3C-formatted id, <b>regardless of whether the caller supplied a raw traceparent string</b>. This is
    /// what makes propagation work for the primary OpenTelemetry use case: an application that has already
    /// adopted OpenTelemetry (e.g. ASP.NET Core instrumentation) sets <see cref="Activity.Current"/> and
    /// never passes a raw header, so gating header emission on a caller-supplied value alone would silently
    /// drop the trace at the process boundary. Falls back to the validated caller-supplied or configured
    /// traceparent only when no activity exists (e.g. no <see cref="ActivityListener"/> is observing
    /// <see cref="XyoTelemetry.ActivitySource"/>). Never emits a non-W3C value: under a legacy
    /// <see cref="ActivityIdFormat.Hierarchical"/> configuration, <see cref="Activity.Id"/> is not a valid
    /// traceparent, so that case falls through to the validated fallback instead of being sent verbatim.
    /// </remarks>
    private static string? ResolveOutboundTraceparent(Activity? activity, string? traceparent, XyoClientConfig config)
    {
        string? effectiveTraceparent = !string.IsNullOrWhiteSpace(traceparent) ? traceparent : config.Traceparent;
        if (!string.IsNullOrWhiteSpace(effectiveTraceparent))
        {
            // Validated unconditionally, even when the SDK's own activity id (below) turns out to be what is
            // actually sent: a malformed caller-supplied or configured traceparent is a caller error and must
            // surface as one, not be silently swallowed just because a span happened to be active.
            ValidateHeaderValue(effectiveTraceparent, nameof(traceparent));
            if (!TraceparentRegex.IsMatch(effectiveTraceparent))
            {
                throw new ArgumentException(
                    "Header 'traceparent' does not conform to the W3C TraceContext format (version-traceid-parentid-flags).", nameof(traceparent));
            }
        }

        if (activity is { IdFormat: ActivityIdFormat.W3C, Id: { } activityId })
        {
            return activityId;
        }

        return string.IsNullOrWhiteSpace(effectiveTraceparent) ? null : effectiveTraceparent;
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
        XyoClientConfig config,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(config.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            return await _httpClient.SendAsync(request, completionOption, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new XyoNetworkException($"Network request timed out after {config.Timeout.TotalSeconds} seconds.", ex);
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

    /// <summary>
    /// Starts a client <see cref="Activity"/> for a public operation, parented to the trace carried by
    /// <paramref name="effectiveTraceparent"/> when one is present and parseable. Returns <c>null</c>, with no
    /// allocation, whenever nothing is listening to the <see cref="XyoTelemetry.ActivitySource"/> -- see
    /// <see cref="XyoTelemetry"/>.
    /// </summary>
    private static Activity? StartClientActivity(string operationName, string? effectiveTraceparent)
    {
        if (!string.IsNullOrWhiteSpace(effectiveTraceparent) &&
            ActivityContext.TryParse(effectiveTraceparent, traceState: null, out var parentContext))
        {
            return XyoTelemetry.ActivitySource.StartActivity(operationName, ActivityKind.Client, parentContext);
        }

        return XyoTelemetry.ActivitySource.StartActivity(operationName, ActivityKind.Client);
    }

    /// <summary>
    /// Maps an operation's outcome to the low-cardinality <c>xyo.sdk.outcome</c> tag value shared by the
    /// request counter, duration histogram, and structured log messages.
    /// </summary>
    private static string ClassifyOutcome(Exception? exception) => exception switch
    {
        null => "ok",
        RateLimitException => "rate_limited",
        OperationCanceledException => "cancelled",
        XyoServerException => "server_error",
        XyoClientException => "client_error",
        XyoNetworkException => "network_error",
        _ => "error"
    };

    /// <summary>
    /// Sets the final <see cref="ActivityStatusCode"/> on a client span from the typed exception hierarchy, so
    /// a <see cref="RateLimitException"/> is distinguishable from a <see cref="XyoNetworkException"/> in the
    /// trace backend.
    /// </summary>
    /// <remarks>
    /// The status description is <see cref="ClassifyOutcome"/>'s low-cardinality, SDK-authored outcome
    /// string, deliberately <b>not</b> <c><paramref name="exception"/>.Message</c>. That message embeds a
    /// prefix of the raw server response body (<c>SafeSummary</c>), the RFC 7807 <c>detail</c> string, and
    /// (for status-lookup failures) the enrichment job id from the request URI -- any of which can carry
    /// transaction data or, from a gateway that echoes request headers into an error body, the bearer token
    /// itself. Running that text through <see cref="LogSafeText.Summarize"/> defeats log forgery but does
    /// not redact its contents, so it is not sufficient here: a trace backend is a wider audience and a
    /// longer retention window than an application log. Full fidelity stays available to a caller who opts
    /// in, via <c>RawResponseBody</c> on the SDK's exception types and <see cref="Exception.Message"/> itself
    /// -- neither is truncated or altered, only kept off the span.
    /// </remarks>
    private static void CompleteActivityStatus(Activity? activity, Exception? exception)
    {
        if (activity is null)
        {
            return;
        }

        if (exception is null)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, ClassifyOutcome(exception));
        activity.SetTag("error.type", exception.GetType().FullName);

        if (exception is RateLimitException { RetryAfter: { } retryAfterSeconds })
        {
            activity.SetTag("xyo.sdk.rate_limit.retry_after_seconds", retryAfterSeconds);
        }
    }

    /// <summary>
    /// Completes a <see cref="StreamEnrichmentCollectionAsync"/> span: the SDK-specific attributes from the
    /// proposed scope (archive entry count, bytes inflated, redirect hop count), standard OpenTelemetry
    /// <c>http.request.resend_count</c> when redirects were followed, plus, on failure, the raw
    /// bytes transferred before the failure -- see <see cref="StreamProgress"/> and
    /// <see cref="ArchiveTransferStatistics"/>.
    /// </summary>
    /// <param name="activity">The client span for this call, or <c>null</c> when nobody is listening.</param>
    /// <param name="exception">The failure that ended the stream, or <c>null</c> on a successful drain.</param>
    /// <param name="progress">Redirect hop count and, once connected, the idle-timeout stream wrapper.</param>
    /// <param name="statistics">Archive entry count and inflated byte progress.</param>
    /// <param name="abandoned">
    /// True when the consumer stopped enumerating without the archive fully draining and without an
    /// exception (<c>break</c>, <c>.Take(n)</c>, <c>.FirstOrDefaultAsync()</c>). The span status is still
    /// completed as <see cref="ActivityStatusCode.Ok"/> (nothing failed), but tagged separately so an
    /// operator can distinguish "the consumer chose to stop early" from "the archive was fully consumed".
    /// </param>
    private static void CompleteStreamActivity(Activity? activity, Exception? exception, StreamProgress progress, ArchiveTransferStatistics statistics, bool abandoned = false)
    {
        if (activity is not null)
        {
            activity.SetTag("xyo.sdk.download.redirect_hop_count", progress.RedirectHopCount);
            if (progress.RedirectHopCount > 0)
            {
                activity.SetTag("http.request.resend_count", progress.RedirectHopCount);
            }
            activity.SetTag("xyo.sdk.archive.entry_count", statistics.EntryCount);
            activity.SetTag("xyo.sdk.archive.bytes_inflated", statistics.InflatedBytes);
            if (progress.IdleStream is not null)
            {
                activity.SetTag("xyo.sdk.download.bytes_transferred", progress.IdleStream.TotalBytesRead);
            }
            if (abandoned)
            {
                activity.SetTag("xyo.sdk.stream.abandoned", true);
            }
        }

        CompleteActivityStatus(activity, exception);
    }

    /// <summary>
    /// Records the request counter and duration histogram (tagged by operation and outcome), plus the
    /// rate-limit counter when the outcome was a <see cref="RateLimitException"/>. Every call is guarded by
    /// the instrument's <c>Enabled</c> property, so nothing here allocates a tag list when no
    /// <c>MeterListener</c> is observing <see cref="XyoTelemetry.Meter"/>.
    /// </summary>
    /// <param name="operationName">The public operation this call belongs to, e.g. <c>EnrichTransaction</c>.</param>
    /// <param name="startTimestamp">A <see cref="Stopwatch.GetTimestamp"/> value captured when the call began.</param>
    /// <param name="exception">The failure that ended the call, or <c>null</c> on success.</param>
    /// <param name="outcomeOverride">
    /// When set, used as the <c>xyo.sdk.outcome</c> tag value instead of <see cref="ClassifyOutcome"/>'s
    /// mapping of <paramref name="exception"/>. Used for the abandoned-stream outcome (see
    /// <see cref="StreamEnrichmentCollectionAsync"/>), which is neither a success nor any exception-derived
    /// failure and so has no <see cref="Exception"/> to classify from.
    /// </param>
    private static void RecordRequestMetrics(string operationName, long startTimestamp, Exception? exception, string? outcomeOverride = null)
    {
        string outcome = outcomeOverride ?? ClassifyOutcome(exception);

        if (XyoTelemetry.RequestCount.Enabled || XyoTelemetry.RequestDuration.Enabled)
        {
            var tags = new TagList
            {
                { "xyo.sdk.operation", operationName },
                { "xyo.sdk.outcome", outcome }
            };

            if (XyoTelemetry.RequestCount.Enabled)
            {
                XyoTelemetry.RequestCount.Add(1, tags);
            }

            if (XyoTelemetry.RequestDuration.Enabled)
            {
                XyoTelemetry.RequestDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, tags);
            }
        }

        if (exception is RateLimitException && XyoTelemetry.RateLimitCount.Enabled)
        {
            XyoTelemetry.RateLimitCount.Add(1, new KeyValuePair<string, object?>("xyo.sdk.operation", operationName));
        }
    }

    /// <summary>
    /// Logs an operation failure through <see cref="_logger"/>, currently always a no-op <c>NullLogger</c>
    /// (see the comment on this type's constructor: the SDK-owned logger source was dropped in favour of
    /// PR #36's <c>ILogger&lt;XyoClient&gt;</c> DI injection, pending this branch's rebase onto it). Only the
    /// operation name, exception type, and exception message reach the log record; none of those ever
    /// contain the API key or the raw Authorization header.
    /// </summary>
    private void LogOperationFailure(string operationName, Exception exception)
    {
        switch (exception)
        {
            case RateLimitException rateLimitException:
                _logger.LogWarning(
                    "XYO SDK operation {Operation} was rate limited; retry after {RetryAfterSeconds}s.",
                    operationName, rateLimitException.RetryAfter);
                break;
            case XyoNetworkException:
                _logger.LogWarning(exception, "XYO SDK operation {Operation} failed with a network error.", operationName);
                break;
            case XyoServerException:
                _logger.LogError(exception, "XYO SDK operation {Operation} failed with an upstream server error.", operationName);
                break;
            case XyoClientException:
                _logger.LogWarning(exception, "XYO SDK operation {Operation} failed with a client error.", operationName);
                break;
            case OperationCanceledException:
                break;
            default:
                _logger.LogError(exception, "XYO SDK operation {Operation} failed unexpectedly.", operationName);
                break;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    /// <summary>
    /// Disposes the underlying HttpClient if owned by this client, and unsubscribes from
    /// <see cref="IOptionsMonitor{TOptions}"/> reload notifications if this instance was constructed to
    /// track them.
    /// </summary>
    public void Dispose()
    {
        // Interlocked.Exchange makes the check-and-set atomic: two threads racing Dispose() against each
        // other, or against an in-flight request's ThrowIfDisposed check, can no longer observe a
        // partially-disposed state (the plain bool check-then-act this replaced had that gap).
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _optionsChangeSubscription?.Dispose();

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
        private long _totalBytesRead;

        /// <summary>
        /// Total bytes read through this wrapper so far, exposed so telemetry can record how much of an
        /// archive transferred before a stall or budget failure (see
        /// <see cref="XyoClient.CompleteStreamActivity"/>).
        /// </summary>
        internal long TotalBytesRead => _totalBytesRead;

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
            if (_idleTimeout == Timeout.InfiniteTimeSpan)
            {
                long start = Stopwatch.GetTimestamp();
                int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                _totalBytesRead += read;
                _cumulativeReadTicks += Stopwatch.GetTimestamp() - start;
                ThrowIfTotalBudgetExceeded();
                return read;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_idleTimeout);

            long startTimestamp = Stopwatch.GetTimestamp();
            try
            {
                int read = await _inner.ReadAsync(buffer, cts.Token).ConfigureAwait(false);

                // Accumulated on the success path only. The stall path below throws regardless, and adding to
                // the budget from a finally block would let a budget violation replace the stall exception
                // already propagating out.
                _totalBytesRead += read;
                _cumulativeReadTicks += Stopwatch.GetTimestamp() - startTimestamp;
                ThrowIfTotalBudgetExceeded();
                return read;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (XyoTelemetry.DownloadBoundTrippedCount.Enabled)
                {
                    XyoTelemetry.DownloadBoundTrippedCount.Add(1, new KeyValuePair<string, object?>(XyoTelemetry.BoundTagKey, XyoTelemetry.BoundIdleTimeout));
                }
                throw new XyoNetworkException(
                    $"Archive download read stalled: the peer did not produce further data within " +
                    $"{_idleTimeout.TotalSeconds} seconds.", ex);
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
                if (XyoTelemetry.DownloadBoundTrippedCount.Enabled)
                {
                    XyoTelemetry.DownloadBoundTrippedCount.Add(1, new KeyValuePair<string, object?>(XyoTelemetry.BoundTagKey, XyoTelemetry.BoundTotalDuration));
                }
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
