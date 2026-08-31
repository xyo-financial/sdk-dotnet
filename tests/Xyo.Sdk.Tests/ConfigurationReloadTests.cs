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

        // Asserted on the exception type plus the configured timeout value rather than the exact sentence:
        // PR #35 splits this single message into separate connect-phase and read-stall wording, and pinning
        // the literal string here would make this test a rebase hazard rather than a regression guard.
        Assert.Contains("0.05", ex.Message);
        Assert.Contains("second", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task OptionsReload_ChangeToADifferentlyNamedXyoClientOptions_DoesNotReconfigureTheDefaultClient()
    {
        // Regression test for US-DOTNET-004 / C1: IOptionsMonitor<T>.OnChange fires for every named T
        // registered in the container, not only the default-named instance a client was constructed from.
        // TestOptionsMonitor cannot exercise this at all (it always reports a null name -- see its remarks),
        // so this test drives the real Microsoft.Extensions.Options infrastructure via a manually registered,
        // independently triggerable IOptionsChangeTokenSource<XyoClientOptions> named "Other".
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK,
            @"{ ""merchant"": ""M"", ""description"": ""D"", ""categories"": [""General""], ""logo"": ""https://cdn.xyo.financial/logo.png"", ""location"": ""London, UK"", ""address"": ""1 High St"" }");

        var services = new ServiceCollection();
        services.Configure<XyoClientOptions>(o =>
        {
            o.ApiKey = "default_key";
            o.BaseUrl = "https://api.xyo.financial";
        });
        services.Configure<XyoClientOptions>("Other", o =>
        {
            o.ApiKey = "other_key";
            o.BaseUrl = "https://sandbox.xyo.financial";
        });

        var otherSource = new TriggerableChangeTokenSource<XyoClientOptions>("Other");
        services.AddSingleton<IOptionsChangeTokenSource<XyoClientOptions>>(otherSource);

        using var serviceProvider = services.BuildServiceProvider();
        var optionsMonitor = serviceProvider.GetRequiredService<IOptionsMonitor<XyoClientOptions>>();

        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(optionsMonitor, httpClient);

        await client.EnrichTransactionAsync("Uber", "GB");
        Assert.StartsWith("https://api.xyo.financial", handler.CapturedRequests[0].RequestUri!.ToString());
        Assert.Equal("default_key", handler.CapturedRequests[0].Headers.Authorization?.Parameter);

        // Fires the change token belonging only to the "Other" named XyoClientOptions instance. Before the
        // fix, XyoClient.OnOptionsChanged adopted this unconditionally: the default-named client ended up
        // sending "other_key" to sandbox.xyo.financial, credential misrouting reproduced against the live PR.
        otherSource.Trigger();

        await client.EnrichTransactionAsync("Uber", "GB");
        Assert.StartsWith("https://api.xyo.financial", handler.CapturedRequests[1].RequestUri!.ToString());
        Assert.Equal("default_key", handler.CapturedRequests[1].Headers.Authorization?.Parameter);
    }

    [Fact]
    public void Dispose_UnsubscribesFromTheOptionsMonitor()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, "{}");
        using var httpClient = new HttpClient(handler);
        var monitor = new TestOptionsMonitor<XyoClientOptions>(new XyoClientOptions { ApiKey = "xyo_test_key" });

        var client = new XyoClient(monitor, httpClient);
        Assert.Equal(1, monitor.ListenerCount);

        client.Dispose();

        Assert.Equal(0, monitor.ListenerCount);
    }
}
