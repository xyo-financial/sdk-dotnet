using System;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddXyoClient("xyo_di_key_123");

        using var serviceProvider = services.BuildServiceProvider();
        var client = serviceProvider.GetRequiredService<IXyoClient>();

        Assert.NotNull(client);
        Assert.IsType<XyoClient>(client);
    }

    [Fact]
    public void AddXyoClient_WithOptionsDelegate_RegistersAndAppliesOptions()
    {
        var services = new ServiceCollection();
        services.AddXyoClient(options =>
        {
            options.ApiKey = "xyo_custom_key";
            options.BaseUrl = "https://sandbox.xyo.financial";
            options.CorrelationId = "custom_trace_id";
            options.Timeout = TimeSpan.FromSeconds(45);
        });

        using var serviceProvider = services.BuildServiceProvider();
        var client = serviceProvider.GetRequiredService<IXyoClient>();
        var options = serviceProvider.GetRequiredService<IOptions<XyoClientOptions>>().Value;

        Assert.NotNull(client);
        Assert.Equal("xyo_custom_key", options.ApiKey);
        Assert.Equal("https://sandbox.xyo.financial", options.BaseUrl);
        Assert.Equal("custom_trace_id", options.CorrelationId);
        Assert.Equal(TimeSpan.FromSeconds(45), options.Timeout);
    }
}
