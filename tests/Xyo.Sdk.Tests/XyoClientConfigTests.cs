using System;
using System.Collections.Generic;
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

    [Fact]
    public void DefaultHeaders_MutatingCallersDictionaryAfterConstruction_DoesNotAffectConfig()
    {
        var callerDict = new Dictionary<string, string> { ["X-Tenant"] = "acme" };
        var config = new XyoClientConfig("key") { DefaultHeaders = callerDict };

        callerDict["X-Evil"] = "ok\r\nX-Injected: pwned";
        callerDict["X-Tenant"] = "tampered";

        Assert.False(config.DefaultHeaders.ContainsKey("X-Evil"));
        Assert.Equal("acme", config.DefaultHeaders["X-Tenant"]);
    }

    [Fact]
    public void DefaultHeaders_ValidatesEveryEntry_EvenWithoutOrdinalIgnoreCaseComparer()
    {
        // A caller-supplied dictionary using a different (or no) comparer must not bypass validation,
        // and the stored copy should still de-duplicate case-insensitively like the type's own default.
        var callerDict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Tenant"] = "acme"
        };

        var config = new XyoClientConfig("key") { DefaultHeaders = callerDict };

        Assert.True(config.DefaultHeaders.ContainsKey("x-tenant"));
    }

    [Fact]
    public void TrustedDownloadHosts_MutatingCallersListAfterConstruction_DoesNotAffectConfig()
    {
        var callerList = new List<string> { "storage.internal.bank.corp" };
        var config = new XyoClientConfig("key") { TrustedDownloadHosts = callerList };

        callerList.Add("attacker.example.com");

        Assert.DoesNotContain("attacker.example.com", config.TrustedDownloadHosts);
    }
}
