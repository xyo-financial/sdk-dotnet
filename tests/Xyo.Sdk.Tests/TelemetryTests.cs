using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using Xyo.Sdk.Client;
using Xyo.Sdk.Exceptions;
using Xyo.Sdk.Telemetry;

namespace Xyo.Sdk.Tests;

/// <summary>
/// Covers the OpenTelemetry instrumentation added for US-DOTNET-005: span creation and status mapping, trace
/// parenting, metric recording, the zero-allocation contract when nobody is listening, and the hard rule that
/// no credential ever reaches a span attribute, a metric tag, or a log message.
/// </summary>
public class TelemetryTests
{
    private static (ActivityListener Listener, List<Activity> Activities) ListenToXyoActivities()
    {
        var activities = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == XyoTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (activities)
                {
                    activities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, activities);
    }

    private static (MeterListener Listener, List<(string Name, object? Value, KeyValuePair<string, object?>[] Tags)> Measurements) ListenToXyoMetrics()
    {
        var measurements = new List<(string, object?, KeyValuePair<string, object?>[])>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, source) =>
            {
                if (instrument.Meter.Name == XyoTelemetry.Name)
                {
                    source.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            lock (measurements)
            {
                measurements.Add((instrument.Name, measurement, tags.ToArray()));
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            lock (measurements)
            {
                measurements.Add((instrument.Name, measurement, tags.ToArray()));
            }
        });
        listener.Start();
        return (listener, measurements);
    }

    private const string EnrichmentJson =
        @"{ ""merchant"": ""Costa"", ""description"": ""D"", ""categories"": [""General""], ""logo"": ""L"", ""location"": ""London"", ""address"": ""1 High St"" }";

    // ---------- Hard requirement: zero allocation when nobody is listening ----------

    [Fact]
    public void ActivitySource_StartActivity_NoListener_AllocatesNoActivity()
    {
        Assert.False(XyoTelemetry.ActivitySource.HasListeners());

        // Warm up the JIT path before measuring so first-call costs don't pollute the delta being asserted.
        _ = XyoTelemetry.ActivitySource.StartActivity("Warmup", ActivityKind.Client);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var activity = XyoTelemetry.ActivitySource.StartActivity("Probe", ActivityKind.Client);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Null(activity);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task NoListeners_EnrichTransactionAsync_DoesNotSetAnAmbientActivity()
    {
        Assert.False(XyoTelemetry.ActivitySource.HasListeners());

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, EnrichmentJson);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        await client.EnrichTransactionAsync("UBER TRIP", "GB");

        // If XyoClient had allocated and started an Activity, it would have become Activity.Current for the
        // duration of the call; with no listener registered, StartActivity returns null and Activity.Current
        // never moves.
        Assert.Null(Activity.Current);
    }

    // ---------- Tracing: span creation and status mapping ----------

    [Fact]
    public async Task EnrichTransactionAsync_Success_RecordsOkActivityWithHttpAttributes()
    {
        var (listener, recorded) = ListenToXyoActivities();
        using var _l = listener;

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, EnrichmentJson);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        await client.EnrichTransactionAsync("UBER TRIP", "GB");

        var activity = Assert.Single(recorded);
        Assert.Equal(XyoTelemetry.Name, activity.Source.Name);
        Assert.Equal("EnrichTransaction", activity.OperationName);
        Assert.Equal(ActivityKind.Client, activity.Kind);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Equal("POST", activity.GetTagItem("http.request.method"));
        Assert.Equal("api.xyo.financial", activity.GetTagItem("server.address"));
        Assert.Equal(200, activity.GetTagItem("http.response.status_code"));
    }

    [Fact]
    public async Task EnrichTransactionAsync_RateLimited_RecordsErrorActivityAndIncrementsRateLimitCounter()
    {
        var (activityListener, activities) = ListenToXyoActivities();
        using var _a = activityListener;
        var (meterListener, measurements) = ListenToXyoMetrics();
        using var _m = meterListener;

        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("slow down", Encoding.UTF8, "text/plain")
            };
            response.Headers.TryAddWithoutValidation("Retry-After", "5");
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        await Assert.ThrowsAsync<RateLimitException>(() => client.EnrichTransactionAsync("UBER TRIP", "GB"));

        var activity = Assert.Single(activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(typeof(RateLimitException).FullName, activity.GetTagItem("error.type"));
        Assert.Equal(5, activity.GetTagItem("xyo.sdk.rate_limit.retry_after_seconds"));

        Assert.Contains(measurements, m => m.Name == "xyo.sdk.client.rate_limit.count" && Convert.ToInt64(m.Value) == 1);
        Assert.Contains(measurements, m => m.Name == "xyo.sdk.client.request.count" &&
            m.Tags.Any(t => t.Key == "xyo.sdk.outcome" && Equals(t.Value, "rate_limited")));
    }

    [Fact]
    public async Task EnrichTransactionAsync_CallerTraceparent_ChildActivityJoinsTraceAndOutboundHeaderReflectsOwnSpan()
    {
        var (listener, activities) = ListenToXyoActivities();
        using var _l = listener;

        const string callerTraceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, EnrichmentJson);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        await client.EnrichTransactionAsync("UBER TRIP", "GB", (string?)null, callerTraceparent);

        var activity = Assert.Single(activities);
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", activity.TraceId.ToHexString());
        Assert.Equal("00f067aa0ba902b7", activity.ParentSpanId.ToHexString());

        string sentTraceparent = handler.CapturedRequests[0].Headers.GetValues("traceparent").Single();
        Assert.NotEqual(callerTraceparent, sentTraceparent);
        Assert.Contains("4bf92f3577b34da6a3ce929d0e0e4736", sentTraceparent);
        Assert.Equal(activity.Id, sentTraceparent);
    }

    [Fact]
    public async Task EnrichTransactionAsync_NoCallerTraceparent_NoListener_ForwardsNothingButStillSucceeds()
    {
        // Sanity check that the traceparent-parenting change does not disturb the ordinary no-header case.
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, EnrichmentJson);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        await client.EnrichTransactionAsync("UBER TRIP", "GB");

        Assert.False(handler.CapturedRequests[0].Headers.NonValidated.Contains("traceparent"));
    }

    // ---------- Tracing: archive download telemetry ----------

    private static byte[] TarGzOfRecords(int count)
    {
        string RecordJson(int i) =>
            $@"{{ ""merchant"": ""M{i}"", ""description"": ""D"", ""categories"": [""General""], " +
            @"""logo"": ""https://cdn.xyo.financial/logo.png"", ""location"": ""London, UK"", ""address"": ""1 High St"" }";

        using var tarMs = new MemoryStream();
        using (var tarWriter = new TarWriter(tarMs, TarEntryFormat.Pax, leaveOpen: true))
        {
            for (int i = 0; i < count; i++)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{i:000}.json")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(RecordJson(i)))
                };
                tarWriter.WriteEntry(entry);
            }
        }
        tarMs.Position = 0;
        using var gzMs = new MemoryStream();
        using (var gz = new GZipStream(gzMs, CompressionLevel.Optimal, leaveOpen: true))
        {
            tarMs.CopyTo(gz);
        }
        return gzMs.ToArray();
    }

    private sealed class StallingStream : Stream
    {
        private readonly byte[] _data;
        private int _position;
        private bool _stalled;

        public StallingStream(byte[] data) => _data = data;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_stalled && _position >= _data.Length / 2)
            {
                _stalled = true;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            int toCopy = Math.Min(Math.Min(buffer.Length, 16), _data.Length - _position);
            _data.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return toCopy;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task StreamEnrichmentCollectionAsync_StalledDownload_RecordsErrorActivityWithBytesTransferred()
    {
        var (listener, activities) = ListenToXyoActivities();
        using var _l = listener;

        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream(TarGzOfRecords(3)))
            };
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        var config = new XyoClientConfig("xyo_test_token") { DownloadTimeout = TimeSpan.FromMilliseconds(200) };
        using var client = new XyoClient(config, httpClient);

        await Assert.ThrowsAsync<XyoNetworkException>(async () =>
        {
            await foreach (var _ in client.StreamEnrichmentCollectionAsync("https://api.xyo.financial/batches/1.tar.gz"))
            {
            }
        });

        var activity = Assert.Single(activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Contains("stalled", activity.StatusDescription, StringComparison.OrdinalIgnoreCase);

        object? bytesTransferred = activity.GetTagItem("xyo.sdk.download.bytes_transferred");
        Assert.NotNull(bytesTransferred);
        Assert.True(Convert.ToInt64(bytesTransferred) > 0);
    }

    [Fact]
    public async Task StreamEnrichmentCollectionAsync_RedirectRefused_IncrementsRedirectRefusedCounter()
    {
        var (meterListener, measurements) = ListenToXyoMetrics();
        using var _m = meterListener;

        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://attacker.evil.com/archive.tar.gz");
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        await Assert.ThrowsAsync<XyoClientException>(async () =>
        {
            await foreach (var _ in client.StreamEnrichmentCollectionAsync("https://api.xyo.financial/batches/1.tar.gz"))
            {
            }
        });

        Assert.Contains(measurements, m => m.Name == "xyo.sdk.download.redirect_refused.count" && Convert.ToInt64(m.Value) == 1);
    }

    // ---------- Hard requirement: no credential ever reaches telemetry ----------

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
                if (exception != null)
                {
                    Messages.Add(exception.ToString());
                }
            }
        }
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public CapturingLogger Logger { get; } = new();

        public ILogger CreateLogger(string categoryName) => Logger;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Telemetry_AcrossSuccessRateLimitAndRefusedRedirect_NeverContainsTheApiKey()
    {
        const string secretApiKey = "xyo_super_secret_credential_zzz9";

        var (activityListener, activities) = ListenToXyoActivities();
        using var _a = activityListener;
        var (meterListener, measurements) = ListenToXyoMetrics();
        using var _m = meterListener;

        var loggerFactory = new CapturingLoggerFactory();
        var config = new XyoClientConfig(secretApiKey) { LoggerFactory = loggerFactory };

        var okHandler = new MockHttpMessageHandler(HttpStatusCode.OK, EnrichmentJson);
        using (var httpClient = new HttpClient(okHandler))
        using (var client = new XyoClient(config, httpClient))
        {
            await client.EnrichTransactionAsync("UBER TRIP", "GB");
        }

        var rateLimitHandler = new MockHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("slow down", Encoding.UTF8, "text/plain")
            };
            return Task.FromResult(response);
        });
        using (var httpClient = new HttpClient(rateLimitHandler))
        using (var client = new XyoClient(config, httpClient))
        {
            await Assert.ThrowsAsync<RateLimitException>(() => client.EnrichTransactionAsync("UBER TRIP", "GB"));
        }

        var redirectHandler = new MockHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://attacker.evil.com/archive.tar.gz");
            return Task.FromResult(response);
        });
        using (var httpClient = new HttpClient(redirectHandler))
        using (var client = new XyoClient(config, httpClient))
        {
            await Assert.ThrowsAsync<XyoClientException>(async () =>
            {
                await foreach (var _ in client.StreamEnrichmentCollectionAsync("https://api.xyo.financial/batches/1.tar.gz"))
                {
                }
            });
        }

        Assert.NotEmpty(activities);
        foreach (var activity in activities)
        {
            Assert.DoesNotContain(secretApiKey, activity.DisplayName);
            Assert.DoesNotContain(secretApiKey, activity.StatusDescription ?? string.Empty);
            foreach (var tag in activity.TagObjects)
            {
                Assert.DoesNotContain(secretApiKey, tag.Value?.ToString() ?? string.Empty);
            }
        }

        Assert.NotEmpty(measurements);
        foreach (var measurement in measurements)
        {
            foreach (var tag in measurement.Tags)
            {
                Assert.DoesNotContain(secretApiKey, tag.Value?.ToString() ?? string.Empty);
            }
        }

        Assert.NotEmpty(loggerFactory.Logger.Messages);
        foreach (var message in loggerFactory.Logger.Messages)
        {
            Assert.DoesNotContain(secretApiKey, message);
        }
    }
}
