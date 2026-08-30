using System;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
            // XyoClient enforces XyoClientOptions.Timeout / DownloadTimeout itself per call via a linked
            // CancellationTokenSource; HttpClient's own Timeout is a single total deadline that would kill a
            // large archive download mid-stream, so it is left infinite.
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
            var options = sp.GetRequiredService<IOptions<XyoClientOptions>>().Value;
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient(HttpClientName);

            var config = options.ToConfig();
            return new XyoClient(config, httpClient);
        });

        return builder;
    }

    /// <summary>
    /// Adds and configures the XYO Financial SDK client with an explicit <see cref="XyoClientConfig"/>.
    /// </summary>
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
