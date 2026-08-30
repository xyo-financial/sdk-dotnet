using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xyo.Sdk.Client;
using Xyo.Sdk.Exceptions;
using Xyo.Sdk.Extensions;

namespace Xyo.Sdk.Tests;

/// <summary>
/// Covers US-DOTNET-004 (EPIC-004): option B, an <see cref="IOptionsMonitor{TOptions}"/>-driven reload of
/// <see cref="XyoClientOptions"/> reaching an already-resolved, singleton-lifetime <see cref="IXyoClient"/>
/// without reconstructing it, plus the fallback behaviour when a reload fails validation.
/// </summary>
public class ConfigurationReloadTests
{
    private static byte[] SingleRecordTarGz()
    {
        const string recordJson = @"{ ""merchant"": ""M"", ""description"": ""D"", ""categories"": [""General""], " +
            @"""logo"": ""https://cdn.xyo.financial/logo.png"", ""location"": ""London, UK"", ""address"": ""1 High St"" }";

        using var tarMs = new MemoryStream();
        using (var tarWriter = new TarWriter(tarMs, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "000.json")
            {
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(recordJson))
            };
            tarWriter.WriteEntry(entry);
        }
        tarMs.Position = 0;
        using var gzMs = new MemoryStream();
        using (var gz = new GZipStream(gzMs, CompressionLevel.Optimal, leaveOpen: true))
        {
            tarMs.CopyTo(gz);
        }
        return gzMs.ToArray();
    }

    [Fact]
    public async Task OptionsReload_ChangedBaseUrl_ReachesTheNextCallOnTheSameInstance()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK,
            @"{ ""merchant"": ""M"", ""description"": ""D"", ""categories"": [""General""], ""logo"": ""https://cdn.xyo.financial/logo.png"", ""location"": ""London, UK"", ""address"": ""1 High St"" }");
        using var httpClient = new HttpClient(handler);
        var monitor = new TestOptionsMonitor<XyoClientOptions>(new XyoClientOptions { ApiKey = "xyo_test_key", BaseUrl = "https://api.xyo.financial" });
        using var client = new XyoClient(monitor, httpClient);

        await client.EnrichTransactionAsync("Uber", "GB");
        Assert.StartsWith("https://api.xyo.financial", handler.CapturedRequests[0].RequestUri!.ToString());

        // Simulates the change token firing after appsettings.json is edited, without reconstructing client.
        monitor.Change(new XyoClientOptions { ApiKey = "xyo_test_key", BaseUrl = "https://sandbox.xyo.financial" });

        await client.EnrichTransactionAsync("Uber", "GB");
        Assert.StartsWith("https://sandbox.xyo.financial", handler.CapturedRequests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task OptionsReload_ChangedTimeout_TripsOnTheNextCall()
    {
        var handler = new MockHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{ ""merchant"": ""M"", ""description"": ""D"", ""categories"": [""General""], ""logo"": ""https://cdn.xyo.financial/logo.png"", ""location"": ""London, UK"", ""address"": ""1 High St"" }")
            };
        });
        using var httpClient = new HttpClient(handler);
        var monitor = new TestOptionsMonitor<XyoClientOptions>(new XyoClientOptions { ApiKey = "xyo_test_key", Timeout = TimeSpan.FromSeconds(30) });
        using var client = new XyoClient(monitor, httpClient);

        monitor.Change(new XyoClientOptions { ApiKey = "xyo_test_key", Timeout = TimeSpan.FromMilliseconds(50) });

        var ex = await Assert.ThrowsAsync<XyoNetworkException>(() => client.EnrichTransactionAsync("Uber", "GB"));
        Assert.Contains("timed out after 0.05 seconds", ex.Message);
    }

    [Fact]
    public async Task OptionsReload_ChangedDownloadTimeout_TripsOnTheNextDownloadCall()
    {
        var handler = new MockHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(SingleRecordTarGz()) };
        });
        using var httpClient = new HttpClient(handler);
        var monitor = new TestOptionsMonitor<XyoClientOptions>(new XyoClientOptions { ApiKey = "xyo_test_key", DownloadTimeout = TimeSpan.FromSeconds(30) });
        using var client = new XyoClient(monitor, httpClient);

        monitor.Change(new XyoClientOptions { ApiKey = "xyo_test_key", DownloadTimeout = TimeSpan.FromMilliseconds(50) });

        var ex = await Assert.ThrowsAsync<XyoNetworkException>(async () =>
        {
            await foreach (var _ in client.StreamEnrichmentCollectionAsync("https://api.xyo.financial/batches/1.tar.gz"))
            {
            }
        });
        Assert.Contains("Archive download timed out after 0.05 seconds", ex.Message);
    }

    [Fact]
    public async Task OptionsReload_ChangedTrustedDownloadHosts_ReachesTheNextDownloadCall()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(SingleRecordTarGz()) }));
        using var httpClient = new HttpClient(handler);
        var monitor = new TestOptionsMonitor<XyoClientOptions>(new XyoClientOptions { ApiKey = "xyo_test_key" });
        using var client = new XyoClient(monitor, httpClient);

        const string downloadUrl = "https://storage.internal.bank.corp/archives/batch.tar.gz";

        var beforeReload = await Assert.ThrowsAsync<XyoClientException>(async () =>
        {
            await foreach (var _ in client.StreamEnrichmentCollectionAsync(downloadUrl))
            {
            }
        });
        Assert.Contains("not in the trusted domain allowlist", beforeReload.Message);

        monitor.Change(new XyoClientOptions
        {
            ApiKey = "xyo_test_key",
            TrustedDownloadHosts = { "storage.internal.bank.corp" }
        });

        var received = 0;
        await foreach (var _ in client.StreamEnrichmentCollectionAsync(downloadUrl))
        {
            received++;
        }
        Assert.Equal(1, received);
    }

    [Fact]
    public async Task OptionsReload_InvalidBaseUrl_LeavesClientOnLastValidConfig_AndSurfacesTheFailureRatherThanSwallowingIt()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK,
            @"{ ""merchant"": ""M"", ""description"": ""D"", ""categories"": [""General""], ""logo"": ""https://cdn.xyo.financial/logo.png"", ""location"": ""London, UK"", ""address"": ""1 High St"" }");
        using var httpClient = new HttpClient(handler);
        var monitor = new TestOptionsMonitor<XyoClientOptions>(new XyoClientOptions { ApiKey = "xyo_test_key", BaseUrl = "https://api.xyo.financial" });
        using var client = new XyoClient(monitor, httpClient);

        Exception? surfaced = null;
        client.OptionsReloadFailed += (_, ex) => surfaced = ex;

        // ftp is rejected by XyoClientConfig.NormalizeBaseUrl (HTTPS-only, loopback-HTTP excepted).
        monitor.Change(new XyoClientOptions { ApiKey = "xyo_test_key", BaseUrl = "ftp://not-a-valid-scheme" });

        Assert.NotNull(surfaced);
        Assert.IsType<ArgumentException>(surfaced);

        // The client must still be serviceable, using the configuration from before the rejected reload.
        await client.EnrichTransactionAsync("Uber", "GB");
        Assert.StartsWith("https://api.xyo.financial", handler.CapturedRequests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task AddXyoClient_ThroughRealDiRegistration_ObservesAnOptionsMonitorReload()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK,
            @"{ ""merchant"": ""M"", ""description"": ""D"", ""categories"": [""General""], ""logo"": ""https://cdn.xyo.financial/logo.png"", ""location"": ""London, UK"", ""address"": ""1 High St"" }");

        var services = new ServiceCollection();
        services.AddXyoClient(options =>
        {
            options.ApiKey = "xyo_test_key";
            options.BaseUrl = "https://api.xyo.financial";
        })
        .ConfigurePrimaryHttpMessageHandler(() => handler);

        // Overrides the monitor AddXyoClient's factory resolves. Registered after AddXyoClient (which
        // registers the default one via services.Configure), so this is the one GetRequiredService returns --
        // exactly what lets this test drive a reload deterministically instead of touching appsettings.json
        // and waiting on a FileSystemWatcher.
        var monitor = new TestOptionsMonitor<XyoClientOptions>(new XyoClientOptions { ApiKey = "xyo_test_key", BaseUrl = "https://api.xyo.financial" });
        services.AddSingleton<IOptionsMonitor<XyoClientOptions>>(monitor);

        using var serviceProvider = services.BuildServiceProvider();

        // Resolved once, exactly as a singleton would be in a real host.
        var client = serviceProvider.GetRequiredService<IXyoClient>();
        Assert.Equal(ServiceLifetime.Singleton, services.Single(d => d.ServiceType == typeof(IXyoClient)).Lifetime);

        await client.EnrichTransactionAsync("Uber", "GB");
        Assert.StartsWith("https://api.xyo.financial", handler.CapturedRequests[0].RequestUri!.ToString());

        monitor.Change(new XyoClientOptions { ApiKey = "xyo_test_key", BaseUrl = "https://sandbox.xyo.financial" });

        // Re-resolving from a singleton registration must yield the exact same instance -- no reconstruction.
        var sameClient = serviceProvider.GetRequiredService<IXyoClient>();
        Assert.Same(client, sameClient);

        await sameClient.EnrichTransactionAsync("Uber", "GB");
        Assert.StartsWith("https://sandbox.xyo.financial", handler.CapturedRequests[1].RequestUri!.ToString());
    }
}
