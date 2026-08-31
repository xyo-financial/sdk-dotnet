using System;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Xyo.Sdk.Client;
using Xyo.Sdk.Extensions;

namespace Xyo.Sdk.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddXyoClient_WithApiKey_RegistersServiceCorrectly()
    {
        var services = new ServiceCollection();
        var builder = services.AddXyoClient("xyo_di_key_123");

        Assert.NotNull(builder);
        Assert.Equal(ServiceCollectionExtensions.HttpClientName, builder.Name);
        Assert.Equal(ServiceLifetime.Singleton, services.Single(d => d.ServiceType == typeof(IXyoClient)).Lifetime);

        using var serviceProvider = services.BuildServiceProvider();
        var client = serviceProvider.GetRequiredService<IXyoClient>();

        Assert.NotNull(client);
        Assert.IsType<XyoClient>(client);

        var optionsMonitor = serviceProvider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();
        var factoryOptions = optionsMonitor.Get(ServiceCollectionExtensions.HttpClientName);
        Assert.Equal(Timeout.InfiniteTimeSpan, factoryOptions.HandlerLifetime);
    }

    [Fact]
    public void AddXyoClient_WithOptionsDelegate_RegistersAndAppliesOptions()
    {
        var services = new ServiceCollection();
        var builder = services.AddXyoClient(options =>
        {
            options.ApiKey = "xyo_custom_key";
            options.BaseUrl = "https://sandbox.xyo.financial";
            options.CorrelationId = "custom_trace_id";
            options.Timeout = TimeSpan.FromSeconds(45);
        });

        Assert.NotNull(builder);
        Assert.Equal(ServiceCollectionExtensions.HttpClientName, builder.Name);
        Assert.Equal(ServiceLifetime.Singleton, services.Single(d => d.ServiceType == typeof(IXyoClient)).Lifetime);

        using var serviceProvider = services.BuildServiceProvider();
        var client = serviceProvider.GetRequiredService<IXyoClient>();
        var options = serviceProvider.GetRequiredService<IOptions<XyoClientOptions>>().Value;

        Assert.NotNull(client);
        Assert.Equal("xyo_custom_key", options.ApiKey);
        Assert.Equal("https://sandbox.xyo.financial", options.BaseUrl);
        Assert.Equal("custom_trace_id", options.CorrelationId);
        Assert.Equal(TimeSpan.FromSeconds(45), options.Timeout);

        var optionsMonitor = serviceProvider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();
        var factoryOptions = optionsMonitor.Get(ServiceCollectionExtensions.HttpClientName);
        Assert.Equal(Timeout.InfiniteTimeSpan, factoryOptions.HandlerLifetime);
    }

    [Fact]
    public void AddXyoClient_WithExplicitConfig_RegistersAndSetsInfiniteHandlerLifetime()
    {
        var services = new ServiceCollection();
        var config = new XyoClientConfig("xyo_explicit_key");
        var builder = services.AddXyoClient(config);

        Assert.NotNull(builder);
        Assert.Equal(ServiceCollectionExtensions.HttpClientName, builder.Name);
        Assert.Equal(ServiceLifetime.Singleton, services.Single(d => d.ServiceType == typeof(IXyoClient)).Lifetime);

        using var serviceProvider = services.BuildServiceProvider();
        var client = serviceProvider.GetRequiredService<IXyoClient>();

        Assert.NotNull(client);
        Assert.IsType<XyoClient>(client);

        var optionsMonitor = serviceProvider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();
        var factoryOptions = optionsMonitor.Get(ServiceCollectionExtensions.HttpClientName);
        Assert.Equal(Timeout.InfiniteTimeSpan, factoryOptions.HandlerLifetime);
    }

    [Fact]
    public void AddXyoClient_WithExplicitConfig_WithoutLoggerFactory_FallsBackToContainerLoggerFactory()
    {
        var services = new ServiceCollection();
        var testFactory = new TestLoggerFactory();
        services.AddSingleton<ILoggerFactory>(testFactory);

        var config = new XyoClientConfig("xyo_explicit_key");
        services.AddXyoClient(config);

        using var serviceProvider = services.BuildServiceProvider();
        var client = serviceProvider.GetRequiredService<IXyoClient>();

        Assert.NotNull(client);
        Assert.Contains(typeof(XyoClient).FullName!, testFactory.CreatedCategories);
    }

    [Fact]
    public void AddXyoClient_WithExplicitConfig_WithExplicitNullLoggerFactory_PreservesExplicitChoice()
    {
        var services = new ServiceCollection();
        var testFactory = new TestLoggerFactory();
        services.AddSingleton<ILoggerFactory>(testFactory);

        var config = new XyoClientConfig("xyo_explicit_key")
        {
            LoggerFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance
        };
        services.AddXyoClient(config);

        using var serviceProvider = services.BuildServiceProvider();
        var client = serviceProvider.GetRequiredService<IXyoClient>();

        Assert.NotNull(client);
        Assert.DoesNotContain(typeof(XyoClient).FullName!, testFactory.CreatedCategories);
    }

    private sealed class TestLoggerFactory : ILoggerFactory
    {
        public System.Collections.Generic.List<string> CreatedCategories { get; } = new();

        public ILogger CreateLogger(string categoryName)
        {
            CreatedCategories.Add(categoryName);
            return Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }
}

