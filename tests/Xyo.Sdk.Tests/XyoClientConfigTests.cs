using System;
using Xunit;
using Xyo.Sdk.Client;

namespace Xyo.Sdk.Tests;

public class XyoClientConfigTests
{
    private const string EnvVarName = "XYO_API_BASE_URL";

    [Fact]
    public void Construction_ExplicitBaseUrl_NotPreemptedByInvalidEnvironmentVariable()
    {
        string? original = Environment.GetEnvironmentVariable(EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(EnvVarName, "http://internal-proxy:8080");

            var config = new XyoClientConfig("key") { BaseUrl = "https://api.xyo.financial" };

            Assert.Equal("https://api.xyo.financial", config.BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVarName, original);
        }
    }

    [Fact]
    public void XyoClientCtor_InvalidEnvironmentDefault_ThrowsWithClearMessage()
    {
        string? original = Environment.GetEnvironmentVariable(EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(EnvVarName, "http://internal-proxy:8080");

            var config = new XyoClientConfig("key");

            var ex = Assert.Throws<ArgumentException>(() => new XyoClient(config));
            Assert.Contains("XYO_API_BASE_URL", ex.Message);
            Assert.Contains("http://internal-proxy:8080", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVarName, original);
        }
    }

    [Fact]
    public void WithBaseUrl_HttpNonLoopback_Throws()
    {
        var config = new XyoClientConfig("key");
        var ex = Assert.Throws<ArgumentException>(() => config.WithBaseUrl("http://internal-gateway"));
        Assert.Contains("must use HTTPS", ex.Message);
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://[::1]")]
    public void WithBaseUrl_HttpLoopback_Allowed(string baseUrl)
    {
        var config = new XyoClientConfig("key").WithBaseUrl(baseUrl);
        Assert.Equal(baseUrl, config.BaseUrl);
    }

    [Fact]
    public void ToString_NeverContainsApiKey()
    {
        var config = new XyoClientConfig("SUPER_SECRET_TOKEN");
        Assert.DoesNotContain("SUPER_SECRET_TOKEN", config.ToString());
    }
}
