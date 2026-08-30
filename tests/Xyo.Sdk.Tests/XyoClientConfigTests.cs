using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xyo.Sdk.Client;
using Xyo.Sdk.Extensions;

namespace Xyo.Sdk.Tests;

public class XyoClientConfigTests
{
    private const string EnvVarName = "XYO_API_BASE_URL";

    [Fact]
    public void DefaultHeaders_CrlfInValueViaInit_Throws()
    {
        var headers = new Dictionary<string, string> { ["X-Tenant"] = "ok\r\nX-Injected: pwned" };
        var ex = Assert.Throws<ArgumentException>(() => new XyoClientConfig("key") { DefaultHeaders = headers });
        Assert.Contains("CRLF injection", ex.Message);
    }

    [Fact]
    public void DefaultHeaders_CrlfInKeyViaInit_Throws()
    {
        var headers = new Dictionary<string, string> { ["X-Tenant\r\nX-Injected"] = "pwned" };
        var ex = Assert.Throws<ArgumentException>(() => new XyoClientConfig("key") { DefaultHeaders = headers });
        Assert.Contains("CRLF injection", ex.Message);
    }

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
    public void ToConfig_InvalidEnvironmentDefault_ThrowsWithSameHintAsDirectConstruction()
    {
        // ToConfig validates BaseUrl through the init accessor, which throws before XyoClient's constructor
        // is ever reached. Without the hint being attached here too, the DI path -- the primary one for
        // hosted applications -- reported only "must use HTTPS" against a URL the caller never wrote down.
        string? original = Environment.GetEnvironmentVariable(EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(EnvVarName, "http://internal-proxy:8080");

            var ex = Assert.Throws<ArgumentException>(() => new XyoClientOptions().ToConfig());

            Assert.Contains("XYO_API_BASE_URL", ex.Message);
            Assert.Contains("http://internal-proxy:8080", ex.Message);
            Assert.Contains("XyoClientOptions.BaseUrl", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVarName, original);
        }
    }

    [Fact]
    public void AddXyoClient_InvalidEnvironmentDefault_SurfacesTheEnvironmentVariableHint()
    {
        string? original = Environment.GetEnvironmentVariable(EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(EnvVarName, "http://internal-proxy:8080");

            var services = new ServiceCollection();
            services.AddXyoClient(options => options.ApiKey = "key");
            using var provider = services.BuildServiceProvider();

            var ex = Assert.ThrowsAny<Exception>(() => provider.GetRequiredService<IXyoClient>());

            // Asserted across the whole chain rather than on a single link: the hint is attached by
            // ToConfig, the un-hinted NormalizeBaseUrl failure is retained as its inner exception, and the
            // DI container is free to wrap either. What matters is that an operator reading the exception
            // sees the environment variable named somewhere.
            var messages = new List<string>();
            for (Exception? current = ex; current != null; current = current.InnerException)
            {
                messages.Add(current.Message);
            }

            Assert.Contains(messages, m => m.Contains("XYO_API_BASE_URL", StringComparison.Ordinal));
            Assert.Contains(messages, m => m.Contains("http://internal-proxy:8080", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVarName, original);
        }
    }

    [Fact]
    public void ToConfig_ExplicitBaseUrl_NotPreemptedByInvalidEnvironmentVariable()
    {
        string? original = Environment.GetEnvironmentVariable(EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(EnvVarName, "http://internal-proxy:8080");

            var config = new XyoClientOptions { BaseUrl = "https://sandbox.xyo.financial" }.ToConfig();

            Assert.Equal("https://sandbox.xyo.financial", config.BaseUrl);
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

    [Theory]
    [InlineData("https://api.xyo.financial#frag")]
    [InlineData("https://api.xyo.financial?trace=1")]
    [InlineData("https://user:pass@api.xyo.financial")]
    public void WithBaseUrl_UserInfoQueryOrFragment_Throws(string baseUrl)
    {
        var config = new XyoClientConfig("key");
        Assert.Throws<ArgumentException>(() => config.WithBaseUrl(baseUrl));
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

    [Fact]
#pragma warning disable CS0618 // Exercising the obsolete alias is the point of this test.
    public void DownloadTimeout_WhenSetAlone_SeedsBothReplacementProperties()
    {
        // Gherkin: "The obsolete setting still works" -- a configuration that sets only the obsolete
        // DownloadTimeout must have both DownloadConnectTimeout and ReadIdleTimeout take that value.
        var config = new XyoClientConfig("key") { DownloadTimeout = TimeSpan.FromMinutes(3) };

        Assert.Equal(TimeSpan.FromMinutes(3), config.DownloadConnectTimeout);
        Assert.Equal(TimeSpan.FromMinutes(3), config.ReadIdleTimeout);
    }

    [Fact]
    public void DownloadConnectTimeoutAndReadIdleTimeout_ExplicitValues_OverrideTheObsoleteAlias()
    {
        // An explicit DownloadConnectTimeout/ReadIdleTimeout must win over DownloadTimeout regardless of the
        // order the two are set in, since a caller migrating away from the obsolete property may still have
        // it configured elsewhere (e.g. a shared base configuration).
        var config = new XyoClientConfig("key")
        {
            DownloadTimeout = TimeSpan.FromMinutes(3),
            DownloadConnectTimeout = TimeSpan.FromMinutes(7),
            ReadIdleTimeout = TimeSpan.FromSeconds(45)
        };

        Assert.Equal(TimeSpan.FromMinutes(7), config.DownloadConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(45), config.ReadIdleTimeout);
    }
#pragma warning restore CS0618

    [Fact]
    public void DownloadConnectTimeoutAndReadIdleTimeout_Unset_UseTheirOwnIndependentDefaults()
    {
        var config = new XyoClientConfig("key");

        Assert.Equal(TimeSpan.FromMinutes(10), config.DownloadConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), config.ReadIdleTimeout);
    }

    [Fact]
#pragma warning disable CS0618 // Exercising the obsolete alias is the point of this test.
    public void ToConfig_ObsoleteDownloadTimeoutAlone_SeedsBothReplacementPropertiesOnConfig()
    {
        var config = new XyoClientOptions
        {
            ApiKey = "key",
            DownloadTimeout = TimeSpan.FromSeconds(90)
        }.ToConfig();
#pragma warning restore CS0618

        Assert.Equal(TimeSpan.FromSeconds(90), config.DownloadConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(90), config.ReadIdleTimeout);
    }

    [Fact]
    public void ToConfig_ExplicitDownloadConnectTimeoutAndReadIdleTimeout_ThreadedThroughIndependently()
    {
        var config = new XyoClientOptions
        {
            ApiKey = "key",
            DownloadConnectTimeout = TimeSpan.FromMinutes(4),
            ReadIdleTimeout = TimeSpan.FromSeconds(30)
        }.ToConfig();

        Assert.Equal(TimeSpan.FromMinutes(4), config.DownloadConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), config.ReadIdleTimeout);
    }
}
