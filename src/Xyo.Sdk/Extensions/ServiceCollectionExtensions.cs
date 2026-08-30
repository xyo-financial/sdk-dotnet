using System;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xyo.Sdk.Client;

namespace Xyo.Sdk.Extensions;

/// <summary>
/// Extension methods for configuring and registering <see cref="IXyoClient"/> with modern .NET Dependency Injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Named HttpClient used by the XYO SDK DI container.
    /// </summary>
    public const string HttpClientName = "XyoClientHttp";

    /// <summary>
    /// Adds and configures the XYO Financial SDK client in the <see cref="IServiceCollection"/>.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="AddXyoClient(IServiceCollection,Action{XyoClientOptions})"/>, so it carries
    /// the same configuration-reload behaviour: <c>IXyoClient</c> is registered as
    /// <see cref="ServiceLifetime.Singleton"/> and stays that way, and if the host also binds
    /// <see cref="XyoClientOptions"/> from a reloadable source (e.g. <c>appsettings.json</c> via
    /// <c>services.Configure&lt;XyoClientOptions&gt;(configuration.GetSection(...))</c>), a later reload
    /// still reaches the already-resolved client -- see that overload's remarks for the full explanation and
    /// EPIC-004 / US-DOTNET-004 for the design decision behind it.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="apiKey">The static API key.</param>
    /// <returns>An <see cref="IHttpClientBuilder"/> that can be used to configure the client handler and resilience policies.</returns>
    public static IHttpClientBuilder AddXyoClient(this IServiceCollection services, string apiKey)
    {
        return services.AddXyoClient(options => options.ApiKey = apiKey);
    }

    /// <summary>
    /// Adds and configures the XYO Financial SDK client in the <see cref="IServiceCollection"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Configuration lifetime:</b> <c>IXyoClient</c> is registered as <see cref="ServiceLifetime.Singleton"/>
    /// and stays that way regardless of how <see cref="XyoClientOptions"/> is later mutated -- this is
    /// deliberate, not a limitation to work around: the previous <c>AddTransient</c> registration leaked,
    /// because <c>IXyoClient</c> is <see cref="IDisposable"/> and the container captured every instance
    /// resolved from the root provider for the lifetime of the application.
    /// </para>
    /// <para>
    /// Despite the singleton lifetime, the resolved client does observe later configuration changes. The
    /// factory below resolves <see cref="IOptionsMonitor{TOptions}"/> rather than a one-shot
    /// <see cref="IOptions{TOptions}"/>, and <see cref="XyoClient"/>'s internal constructor subscribes to its
    /// change token: when the host reloads <see cref="XyoClientOptions"/> (for example because it is bound
    /// from <c>appsettings.json</c> with <c>reloadOnChange: true</c>, the default for
    /// <c>WebApplication.CreateBuilder</c>), the next SDK call made through the already-resolved singleton
    /// uses the new values -- no restart, and no new instance is ever constructed or captured by the
    /// container. Rebuilding the effective <see cref="XyoClientConfig"/> happens only on that change
    /// notification, never per call, so there is no added cost on the hot path.
    /// </para>
    /// <para>
    /// A reload that fails validation (for example an invalid <c>BaseUrl</c>) is rejected: the singleton
    /// keeps serving requests against its last valid configuration, and the failure is logged rather than
    /// swallowed or allowed to crash the process. See <see cref="XyoClient"/>'s internal
    /// <c>OnOptionsChanged</c> handler for the full behaviour this decision relies on.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Delegate to configure client options.</param>
    /// <returns>An <see cref="IHttpClientBuilder"/> that can be used to configure the client handler and resilience policies.</returns>
    public static IHttpClientBuilder AddXyoClient(this IServiceCollection services, Action<XyoClientOptions> configureOptions)
    {
        if (services == null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (configureOptions == null)
        {
            throw new ArgumentNullException(nameof(configureOptions));
        }

        services.Configure(configureOptions);

        var builder = services.AddHttpClient(HttpClientName, client =>
        {
            // XyoClient enforces XyoClientOptions.Timeout / DownloadConnectTimeout / ReadIdleTimeout itself
            // per call via a linked CancellationTokenSource; HttpClient's own Timeout is a single total
            // deadline that would kill a large archive download mid-stream, so it is left infinite.
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // The SDK validates every redirect hop itself against the download allowlist;
            // letting the handler auto-follow would bypass that validation (SSRF).
            AllowAutoRedirect = false
        })
        .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services.AddSingleton<IXyoClient>(sp =>
        {
            // IOptionsMonitor, not IOptions: the latter is read once and frozen at first resolution, which is
            // exactly the reload gap this registration exists to close -- see the remarks above.
            var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<XyoClientOptions>>();
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient(HttpClientName);
            var logger = sp.GetService<ILogger<XyoClient>>();

            return new XyoClient(optionsMonitor, httpClient, logger);
        });

        return builder;
    }

    /// <summary>
    /// Adds and configures the XYO Financial SDK client with an explicit <see cref="XyoClientConfig"/>.
    /// </summary>
    /// <remarks>
    /// <b>Configuration lifetime:</b> unlike the <see cref="Action{XyoClientOptions}"/> overload,
    /// <paramref name="config"/> is not sourced from <see cref="XyoClientOptions"/> or the options system at
    /// all, so there is no reloadable source for the registered client to observe. It is read once, here,
    /// and fixed for the lifetime of the singleton <c>IXyoClient</c> registration; to pick up a later change,
    /// re-register the service (e.g. rebuild the host). This matches the documented behaviour of
    /// <see cref="XyoClient(XyoClientConfig,HttpClient)"/> itself, which this overload calls directly.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="config">The explicit client configuration.</param>
    /// <returns>An <see cref="IHttpClientBuilder"/> that can be used to configure the client handler and resilience policies.</returns>
    public static IHttpClientBuilder AddXyoClient(this IServiceCollection services, XyoClientConfig config)
    {
        if (services == null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        var builder = services.AddHttpClient(HttpClientName, client =>
        {
            // See the other AddXyoClient overload: XyoClient enforces its own per-call deadlines, so the
            // HttpClient-level timeout is left infinite rather than capping the whole connection lifetime.
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // The SDK validates every redirect hop itself against the download allowlist;
            // letting the handler auto-follow would bypass that validation (SSRF).
            AllowAutoRedirect = false
        })
        .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services.AddSingleton<IXyoClient>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient(HttpClientName);
            return new XyoClient(config, httpClient);
        });

        return builder;
    }
}
