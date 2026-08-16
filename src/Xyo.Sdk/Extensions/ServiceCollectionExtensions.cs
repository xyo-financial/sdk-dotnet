using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
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
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddXyoClient(this IServiceCollection services, string apiKey)
    {
        return services.AddXyoClient(options => options.ApiKey = apiKey);
    }

    /// <summary>
    /// Adds and configures the XYO Financial SDK client in the <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Delegate to configure client options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddXyoClient(this IServiceCollection services, Action<XyoClientOptions> configureOptions)
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

        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<XyoClientOptions>>().Value;
            client.Timeout = options.Timeout;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        });

        services.AddTransient<IXyoClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<XyoClientOptions>>().Value;
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient(HttpClientName);

            var config = options.ToConfig();
            return new XyoClient(config, httpClient);
        });

        return services;
    }

    /// <summary>
    /// Adds and configures the XYO Financial SDK client with an explicit <see cref="XyoClientConfig"/>.
    /// </summary>
    public static IServiceCollection AddXyoClient(this IServiceCollection services, XyoClientConfig config)
    {
        if (services == null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        services.AddHttpClient(HttpClientName, client =>
        {
            client.Timeout = config.Timeout;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        });

        services.AddTransient<IXyoClient>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient(HttpClientName);
            return new XyoClient(config, httpClient);
        });

        return services;
    }
}
