using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xyo.Generated.Model;
using Xyo.Sdk.Client;
using Xyo.Sdk.Exceptions;

namespace Xyo.Sdk.Tests;

public class XyoClientTests
{
    [Fact]
    public async Task EnrichTransactionAsync_NullRequiredField_ThrowsWithRawResponseBodyAttached()
    {
        // A payload the API is documented to be able to send (a required field returned as null) must
        // surface as a typed exception carrying the actual payload -- not just a generic message with
        // nothing to inspect to find out which record/field was the problem.
        string jsonResponse = @"
        {
            ""merchant"": ""Costa Coffee"",
            ""description"": ""British coffeehouse chain."",
            ""categories"": [""Food & Dining""],
            ""logo"": ""https://cdn.xyo.financial/logos/costa.png"",
            ""location"": null,
            ""address"": ""40-42 Great Portland St, London W1W 7LZ""
        }";
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jsonResponse);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        var ex = await Assert.ThrowsAsync<XyoServerException>(() => client.EnrichTransactionAsync("Uber", "GB"));

        Assert.Contains("does not conform to the enrichment schema", ex.Message);
        Assert.NotNull(ex.RawResponseBody);
        Assert.Contains("Costa Coffee", ex.RawResponseBody);
    }

    [Fact]
    public async Task EnrichTransactionAsync_ValidInput_ReturnsEnrichedProfile()
    {
        string jsonResponse = @"
        {
            ""merchant"": ""Costa Coffee"",
            ""description"": ""British coffeehouse chain."",
            ""categories"": [""Food & Dining"", ""Coffee Shops""],
            ""logo"": ""https://cdn.xyo.financial/logos/costa.png"",
            ""location"": ""United Kingdom, London"",
            ""address"": ""40-42 Great Portland St, London W1W 7LZ""
        }";

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jsonResponse);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token_123"), httpClient);

        var result = await client.EnrichTransactionAsync("SQ *COSTA COFFEE GREENWICH", "GB");

        Assert.NotNull(result);
        Assert.Equal("Costa Coffee", result.Merchant);
        Assert.Equal("British coffeehouse chain.", result.Description);
        Assert.Equal(2, result.Categories.Count);
        Assert.Equal("https://cdn.xyo.financial/logos/costa.png", result.Logo);

        Assert.Single(handler.CapturedRequests);
        var captured = handler.CapturedRequests[0];
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("xyo_test_token_123", captured.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task EnrichTransactionAsync_EmptyContent_ThrowsArgumentException()
    {
        using var client = new XyoClient("xyo_test_token_123");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.EnrichTransactionAsync("", "GB"));
        Assert.Contains("cannot be null, empty", ex.Message);
    }

    [Fact]
    public async Task EnrichTransactionAsync_ContentExceeding128Chars_ThrowsArgumentException()
    {
        using var client = new XyoClient("xyo_test_token_123");
        string longContent = new string('A', 129);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.EnrichTransactionAsync(longContent, "GB"));
        Assert.Contains("exceeds maximum length of 128", ex.Message);
    }

    [Theory]
    [InlineData("G")]
    [InlineData("GBR")]
    [InlineData("12")]
    [InlineData("G-")]
    public async Task EnrichTransactionAsync_InvalidCountryCode_ThrowsArgumentException(string invalidCountryCode)
    {
        using var client = new XyoClient("xyo_test_token_123");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.EnrichTransactionAsync("Valid Content", invalidCountryCode));
        Assert.Contains("Must be a 2-letter ISO 3166-1 alpha-2", ex.Message);
    }

    [Fact]
    public async Task EnrichTransactionsAsync_ValidBatch_SubmitsSuccessfully()
    {
        string jsonResponse = @"
        {
            ""id"": ""batch_job_998877"",
            ""link"": ""https://download.xyo.financial/batches/998877.tar.gz""
        }";

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jsonResponse);
        using var httpClient = new HttpClient(handler);
        var config = new XyoClientConfig("xyo_test_token_123").WithCorrelationId("corr_trace_001");
        using var client = new XyoClient(config, httpClient);

        var batch = new List<EnrichmentRequest>
        {
            new("UBER TRIP 123", "gb"),
            new("STARBUCKS #405", "us")
        };

        var result = await client.EnrichTransactionsAsync(batch, apiUser: "tenant_bank_alpha");

        Assert.NotNull(result);
        Assert.Equal("batch_job_998877", result.Id);
        Assert.Equal("https://download.xyo.financial/batches/998877.tar.gz", result.Link);

        var captured = handler.CapturedRequests[0];
        Assert.True(captured.Headers.Contains("x-api-user"));
        Assert.Equal("tenant_bank_alpha", captured.Headers.GetValues("x-api-user").First());
        Assert.True(captured.Headers.Contains("X-Correlation-ID"));
        Assert.Equal("corr_trace_001", captured.Headers.GetValues("X-Correlation-ID").First());
    }

    [Fact]
    public async Task EnrichTransactionsAsync_ArrayBatchInput_SubmitsSuccessfully()
    {
        string jsonResponse = @"
        {
            ""id"": ""batch_job_112233"",
            ""link"": ""https://download.xyo.financial/batches/112233.tar.gz""
        }";

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jsonResponse);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token_123"), httpClient);

        EnrichmentRequest[] batchArray = new EnrichmentRequest[]
        {
            new("UBER TRIP 123", "gb"),
            new("STARBUCKS #405", "us")
        };

        var result = await client.EnrichTransactionsAsync(batchArray);

        Assert.NotNull(result);
        Assert.Equal("batch_job_112233", result.Id);
    }

    [Fact]
    public async Task EnrichTransactionsAsync_EmptyBatch_ThrowsArgumentException()
    {
        using var client = new XyoClient("xyo_test_token_123");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.EnrichTransactionsAsync(new List<EnrichmentRequest>()));
        Assert.Contains("cannot be empty", ex.Message);
    }

    [Fact]
    public async Task EnrichTransactionsAsync_CrlfInApiUser_ThrowsArgumentException()
    {
        using var client = new XyoClient("xyo_test_token_123");
        var batch = new List<EnrichmentRequest> { new("COSTA", "GB") };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.EnrichTransactionsAsync(batch, apiUser: "user\r\nInjected-Header: evil"));
        Assert.Contains("forbidden CRLF injection", ex.Message);
    }

    [Fact]
    public async Task EnrichTransactionAsync_CrlfInTraceparent_ThrowsArgumentException()
    {
        var jsonResponse = @"{ ""merchant"": ""Test"", ""description"": ""Test"", ""categories"": [], ""logo"": """", ""location"": """", ""address"": """" }";
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jsonResponse);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.EnrichTransactionAsync("Uber Trip", "GB", (string?)null, "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01\r\nInjected: evil"));
        Assert.Contains("forbidden CRLF injection", ex.Message);
    }

    [Fact]
    public void XyoClientConfig_CrlfInTraceparentInitSetter_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new XyoClientConfig("xyo_test_token") { Traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01\r\nInjected: evil" });
        Assert.Contains("CRLF injection", ex.Message);
    }

    [Fact]
    public void XyoClientConfig_WithTraceparent_MalformedFormat_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new XyoClientConfig("xyo_test_token").WithTraceparent("not-a-valid-traceparent"));
        Assert.Contains("W3C TraceContext", ex.Message);
    }

    [Fact]
    public async Task GetEnrichmentStatusAsync_ValidId_ReturnsStatus()
    {
        string jsonResponse = @"{ ""status"": ""READY"" }";

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jsonResponse);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token_123"), httpClient);

        var result = await client.GetEnrichmentStatusAsync("batch_job_998877");

        Assert.NotNull(result);
        Assert.Equal(EnrichmentCollectionStatusResponse.StatusEnum.READY, result.Status);

        var captured = handler.CapturedRequests[0];
        Assert.Contains("id=batch_job_998877", captured.RequestUri?.Query);
    }

    [Fact]
    public async Task DynamicTokenSupplier_RotatesKeyPerRequest()
    {
        int callCount = 0;
        var config = new XyoClientConfig()
            .WithTokenSupplier(() =>
            {
                callCount++;
                return $"dynamic_token_{callCount}";
            });

        string jsonResponse = @"{ ""merchant"": ""Merchant"", ""description"": ""Desc"", ""categories"": [], ""logo"": ""https://cdn.xyo.financial/logo.png"", ""location"": ""London, UK"", ""address"": ""1 High St"" }";
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jsonResponse);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(config, httpClient);

        await client.EnrichTransactionAsync("Item 1", "GB");
        await client.EnrichTransactionAsync("Item 2", "GB");

        Assert.Equal(2, handler.CapturedRequests.Count);
        Assert.Equal("dynamic_token_1", handler.CapturedRequests[0].Headers.Authorization?.Parameter);
        Assert.Equal("dynamic_token_2", handler.CapturedRequests[1].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task EnrichTransactionAsync_UsesJsonContent_SetsApplicationJsonMediaType()
    {
        string jsonResponse = @"{ ""merchant"": ""Test"", ""description"": ""Test Description"", ""categories"": [""General""], ""logo"": ""https://cdn.xyo.financial/test.png"", ""location"": ""London"", ""address"": ""1 St"" }";
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jsonResponse);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        await client.EnrichTransactionAsync("Uber Trip", "GB");

        Assert.Single(handler.CapturedRequests);
        var captured = handler.CapturedRequests[0];
        Assert.NotNull(captured.Content);
        Assert.Equal("application/json", captured.Content.Headers.ContentType?.MediaType);
        string sentBody = await captured.Content.ReadAsStringAsync();
        Assert.Contains("\"content\":\"Uber Trip\"", sentBody);
        Assert.Contains("\"countryCode\":\"GB\"", sentBody);
    }

    [Fact]
    public async Task EnsureSuccessResponseAsync_MassiveErrorResponse_TruncatesAt32KChars()
    {
        // 128 KB error string
        string massiveError = new string('E', 128 * 1024);
        var handler = new MockHttpMessageHandler(HttpStatusCode.InternalServerError, massiveError);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        var ex = await Assert.ThrowsAsync<XyoServerException>(() => client.EnrichTransactionAsync("Uber", "GB"));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.NotNull(ex.RawResponseBody);
        Assert.Equal(32768, ex.RawResponseBody.Length);
    }

    [Fact]
    public async Task EnsureSuccessResponseAsync_MultiByteUtf8Content_PreservesCharacterBoundariesWithoutCorruption()
    {
        // Multi-byte Unicode sequence (Japanese / emojis / Euro sign)
        string chunk = "⚡ €uro 日本語 🚀 ";
        var sb = new System.Text.StringBuilder();
        while (sb.Length < 40000)
        {
            sb.Append(chunk);
        }
        string massiveUtf8Error = sb.ToString();

        var handler = new MockHttpMessageHandler(HttpStatusCode.InternalServerError, massiveUtf8Error);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        var ex = await Assert.ThrowsAsync<XyoServerException>(() => client.EnrichTransactionAsync("Uber", "GB"));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.NotNull(ex.RawResponseBody);
        Assert.Equal(32768, ex.RawResponseBody.Length);
        Assert.DoesNotContain("\uFFFD", ex.RawResponseBody);
    }

    [Fact]
    public async Task EnsureSuccessResponseAsync_ControlCharactersInBody_FlattenedInMessageButNotInRawResponseBody()
    {
        const char esc = '\u001b';
        const char lineSeparator = '\u2028';
        const char paragraphSeparator = '\u2029';
        string body = $"Payment approved{esc}[31mFAKE{esc}[0m\n2026-08-29 INFO forged line {lineSeparator}sep{paragraphSeparator} \0end";
        var handler = new MockHttpMessageHandler(HttpStatusCode.InternalServerError, body);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        var ex = await Assert.ThrowsAsync<XyoServerException>(() => client.EnrichTransactionAsync("Uber", "GB"));

        // The summary used in Message must not carry any control character an attacker-influenced body
        // could inject: CR/LF (log-line forgery), ESC (ANSI escape injection), U+2028/U+2029 (line
        // separators to JS-based log viewers), or NUL (truncation in C-based sinks).
        Assert.DoesNotContain(esc, ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain(lineSeparator, ex.Message);
        Assert.DoesNotContain(paragraphSeparator, ex.Message);
        Assert.DoesNotContain('\0', ex.Message);

        // Full, unaltered fidelity is still available via RawResponseBody for callers who opt into it.
        Assert.NotNull(ex.RawResponseBody);
        Assert.Contains(esc, ex.RawResponseBody);
        Assert.Contains(lineSeparator, ex.RawResponseBody);
    }

    [Fact]
    public async Task EnsureSuccessResponseAsync_TruncationBoundarySplitsSurrogatePair_DoesNotEmitLoneSurrogate()
    {
        // A surrogate pair (an emoji) placed so it straddles the 512-character clamp boundary.
        string prefix = new string('A', 511);
        string body = prefix + "\U0001F600" + new string('B', 100); // U+1F600 = high+low surrogate pair
        var handler = new MockHttpMessageHandler(HttpStatusCode.InternalServerError, body);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        var ex = await Assert.ThrowsAsync<XyoServerException>(() => client.EnrichTransactionAsync("Uber", "GB"));

        // Whatever character the message ends on (before the ellipsis), it must not be a lone surrogate --
        // some structured-log serializers reject a string containing one as invalid UTF-16 outright.
        string beforeEllipsis = ex.Message.TrimEnd('\u2026');
        Assert.False(char.IsSurrogate(beforeEllipsis[^1]));
    }

    [Fact]
    public async Task SendRequestAsync_Timeout_ThrowsXyoNetworkExceptionWithTimeoutDetails()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing."));
        using var httpClient = new HttpClient(handler);
        var config = new XyoClientConfig("xyo_test_token") { Timeout = TimeSpan.FromSeconds(30) };
        using var client = new XyoClient(config, httpClient);

        var ex = await Assert.ThrowsAsync<XyoNetworkException>(() => client.EnrichTransactionAsync("Uber", "GB", CancellationToken.None));
        Assert.Contains("Network request timed out after 30 seconds.", ex.Message);
        Assert.IsType<TaskCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task SendRequestAsync_IntentionalCancellation_RethrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new MockHttpMessageHandler((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EnrichTransactionAsync("Uber", "GB", cts.Token));
    }

    [Fact]
    public async Task StreamEnrichmentCollectionAsync_Timeout_ThrowsXyoNetworkException()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
            throw new TaskCanceledException("The request was canceled due to timeout."));
        using var httpClient = new HttpClient(handler);
        var config = new XyoClientConfig("xyo_test_token") { DownloadTimeout = TimeSpan.FromSeconds(20) };
        using var client = new XyoClient(config, httpClient);

        var ex = await Assert.ThrowsAsync<XyoNetworkException>(async () =>
        {
            await foreach (var _ in client.StreamEnrichmentCollectionAsync("https://api.xyo.financial/batches/1.tar.gz", CancellationToken.None))
            {
            }
        });
        Assert.Contains("Archive download timed out after 20 seconds.", ex.Message);
    }

    [Fact]
    public async Task StreamEnrichmentCollectionAsync_IntentionalCancellation_RethrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new MockHttpMessageHandler((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamEnrichmentCollectionAsync("https://api.xyo.financial/batches/1.tar.gz", cts.Token))
            {
            }
        });
    }

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
                    DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(RecordJson(i)))
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

    [Fact]
    public async Task StreamEnrichmentCollectionAsync_SlowConsumerBetweenRecords_DoesNotCountAgainstDownloadTimeout()
    {
        // Reproduces the exact scenario a real high-volume pipeline hits: DownloadTimeout is well under
        // what 3 records x slow-consumer-processing-per-record would add up to (600ms x 3 = 1800ms > the
        // 1s DownloadTimeout below), but the network/decompression side is instant. Before the fix, the
        // single wall-clock deadline spanned the whole enumeration and counted the consumer's own
        // Task.Delay against the SDK's timeout; after the fix, only production of the next record is
        // timed, and consumer time between yields is never counted, so this must complete without
        // throwing regardless of how long the caller takes to process each record.
        var mockHandler = new MockHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(TarGzOfRecords(3))
            };
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(mockHandler);
        var config = new XyoClientConfig("xyo_test_token") { DownloadTimeout = TimeSpan.FromSeconds(1) };
        using var client = new XyoClient(config, httpClient);

        int received = 0;
        await foreach (var _ in client.StreamEnrichmentCollectionAsync("https://api.xyo.financial/batches/1.tar.gz", CancellationToken.None))
        {
            received++;
            await Task.Delay(600); // simulated per-record consumer work (e.g. a database write)
        }

        Assert.Equal(3, received);
    }

    private sealed class StallingStream : Stream
    {
        private readonly byte[] _data;
        private int _position;
        private bool _stalled;

        public StallingStream(byte[] data) => _data = data;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Stall forever once past the halfway point, simulating a network read that never completes
            // (a stalled connection) rather than a fast/instant mock response. Checked BEFORE copying, and
            // reads are capped to a small chunk, so this reliably triggers partway through -- a single
            // ReadAsync large enough to consume the whole (tiny, few-hundred-byte) test archive in one call
            // would otherwise never revisit this check at all.
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
    public async Task StreamEnrichmentCollectionAsync_StalledNetworkRead_ThrowsTypedExceptionNotRaw()
    {
        // Before the fix, a stall while reading the archive body (as opposed to the initial SendAsync)
        // was outside any exception translation and would have escaped as a raw
        // OperationCanceledException/TaskCanceledException, bypassing the SDK's typed exception hierarchy.
        var mockHandler = new MockHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream(TarGzOfRecords(3)))
            };
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(mockHandler);
        var config = new XyoClientConfig("xyo_test_token") { DownloadTimeout = TimeSpan.FromMilliseconds(200) };
        using var client = new XyoClient(config, httpClient);

        var ex = await Assert.ThrowsAsync<XyoNetworkException>(async () =>
        {
            await foreach (var _ in client.StreamEnrichmentCollectionAsync("https://api.xyo.financial/batches/1.tar.gz", CancellationToken.None))
            {
            }
        });

        Assert.Contains("stalled", ex.Message);
    }

    [Fact]
    public async Task StreamEnrichmentCollectionAsync_RedirectToUntrustedHost_ThrowsXyoClientException()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://attacker.evil.com/archive.tar.gz");
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        var ex = await Assert.ThrowsAsync<XyoClientException>(async () =>
        {
            await foreach (var _ in client.StreamEnrichmentCollectionAsync("https://api.xyo.financial/batches/1.tar.gz", CancellationToken.None))
            {
            }
        });

        Assert.Contains("not in the trusted domain allowlist", ex.Message);
        // Refused on the redirect itself -- the attacker host is never actually requested.
        Assert.Single(handler.CapturedRequests);
    }

    [Fact]
    public async Task StreamEnrichmentCollectionAsync_RedirectLoop_StopsAtMaxDownloadRedirectsWithAccurateCount()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://api.xyo.financial/batches/next.tar.gz");
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        var ex = await Assert.ThrowsAsync<XyoClientException>(async () =>
        {
            await foreach (var _ in client.StreamEnrichmentCollectionAsync("https://api.xyo.financial/batches/1.tar.gz", CancellationToken.None))
            {
            }
        });

        Assert.Contains($"maximum of {XyoClient.MaxDownloadRedirects} redirects", ex.Message);
        // MaxDownloadRedirects redirects are genuinely followed (that many requests are made and their
        // redirects taken) before the next one trips the cap -- the message's claimed count must match
        // what actually happened, not be off by one.
        Assert.Equal(XyoClient.MaxDownloadRedirects + 1, handler.CapturedRequests.Count);
    }

    [Fact]
    public async Task StreamEnrichmentCollectionAsync_RedirectToExternalStorage_WithholdsTracingHeadersToo()
    {
        static byte[] MinimalTarGz()
        {
            string record = @"{ ""merchant"": ""M"", ""description"": ""D"", ""categories"": [""General""], " +
                @"""logo"": ""https://cdn.xyo.financial/logo.png"", ""location"": ""London, UK"", ""address"": ""1 High St"" }";

            using var tarMs = new MemoryStream();
            using (var tarWriter = new TarWriter(tarMs, TarEntryFormat.Pax, leaveOpen: true))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "001.json")
                {
                    DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(record))
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

        int callCount = 0;
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            callCount++;
            if (callCount == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://xyo-financial.s3.amazonaws.com/batches/1.tar.gz");
                return Task.FromResult(redirect);
            }

            var final = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(MinimalTarGz())
            };
            return Task.FromResult(final);
        });
        using var httpClient = new HttpClient(handler);
        var config = new XyoClientConfig("xyo_test_token")
            .WithCorrelationId("test-corr-id")
            .WithTraceparent("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        using var client = new XyoClient(config, httpClient);

        await foreach (var _ in client.StreamEnrichmentCollectionAsync("https://api.xyo.financial/batches/1.tar.gz", CancellationToken.None))
        {
        }

        Assert.Equal(2, handler.CapturedRequests.Count);

        var internalRequest = handler.CapturedRequests[0];
        Assert.True(internalRequest.Headers.NonValidated.Contains("X-Correlation-ID"));
        Assert.True(internalRequest.Headers.NonValidated.Contains("traceparent"));
        Assert.NotNull(internalRequest.Headers.Authorization);

        var externalRequest = handler.CapturedRequests[1];
        Assert.False(externalRequest.Headers.NonValidated.Contains("X-Correlation-ID"));
        Assert.False(externalRequest.Headers.NonValidated.Contains("traceparent"));
        Assert.Null(externalRequest.Headers.Authorization);
    }

    [Fact]
    public void XyoClient_IsSealed_SatisfiesSonarQubeS3881()
    {
        Assert.True(typeof(XyoClient).IsSealed, "XyoClient must be sealed to conform to SonarQube rule S3881.");
    }

    [Fact]
    public async Task ApplyDefaultHeaders_ContentHeaderAndCustomHeaders_TryAddWithoutValidationAppliesHeadersSuccessfully()
    {
        string jsonResponse = @"{ ""merchant"": ""Test"", ""description"": ""Test"", ""categories"": [], ""logo"": """", ""location"": """", ""address"": """" }";
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jsonResponse);
        using var httpClient = new HttpClient(handler);
        var config = new XyoClientConfig("xyo_test_token")
            .WithCorrelationId("test-corr-id")
            .WithDefaultHeader("Content-Type", "application/json; custom=val")
            .WithDefaultHeader("X-Custom-Tenant", "tenant-123");
        using var client = new XyoClient(config, httpClient);

        var result = await client.EnrichTransactionAsync("Uber Trip", "GB");

        Assert.NotNull(result);
        Assert.Single(handler.CapturedRequests);
        var captured = handler.CapturedRequests[0];
        Assert.True(captured.Headers.Contains("X-Correlation-ID"));
        Assert.Equal("test-corr-id", captured.Headers.GetValues("X-Correlation-ID").First());
        Assert.True(captured.Headers.Contains("X-Custom-Tenant"));
        Assert.Equal("tenant-123", captured.Headers.GetValues("X-Custom-Tenant").First());
    }

    [Fact]
    public async Task TracingHeaders_MethodLevelOverridesAndGuid_SentCorrectly()
    {
        string jsonResponse = @"{ ""merchant"": ""Test"", ""description"": ""Test"", ""categories"": [], ""logo"": """", ""location"": """", ""address"": """" }";
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jsonResponse);
        using var httpClient = new HttpClient(handler);
        var config = new XyoClientConfig("xyo_test_token")
            .WithCorrelationId("config-corr-id")
            .WithTraceparent("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-00");
        using var client = new XyoClient(config, httpClient);

        var testGuid = Guid.NewGuid();
        string traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        await client.EnrichTransactionAsync("Uber Trip", "GB", testGuid, traceparent);

        Assert.Single(handler.CapturedRequests);
        var captured = handler.CapturedRequests[0];
        Assert.True(captured.Headers.Contains("X-Correlation-ID"));
        Assert.Equal(testGuid.ToString(), captured.Headers.GetValues("X-Correlation-ID").First());
        Assert.True(captured.Headers.Contains("traceparent"));
        Assert.Equal(traceparent, captured.Headers.GetValues("traceparent").First());
    }

    [Fact]
    public async Task EnrichTransactionsAsync_Exceeds50kItems_ThrowsArgumentException()
    {
        using var client = new XyoClient("xyo_test_token");
        var largeBatch = new List<EnrichmentRequest>(50001);
        var req = new EnrichmentRequest("UBER", "GB");
        for (int i = 0; i < 50001; i++)
        {
            largeBatch.Add(req);
        }

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.EnrichTransactionsAsync(largeBatch));
        Assert.Contains("exceeds maximum limit of 50,000 items", ex.Message);
    }

    [Fact]
    public async Task Http429_RateLimitHeaders_ParsedIntoRateLimitException()
    {
        string jsonError = @"{ ""title"": ""Too Many Requests"", ""detail"": ""Rate limit exceeded."" }";
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var resp = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent(jsonError, System.Text.Encoding.UTF8, "application/json")
            };
            resp.Headers.Add("Retry-After", "30");
            resp.Headers.Add("RateLimit-Limit", "100");
            resp.Headers.Add("RateLimit-Remaining", "0");
            resp.Headers.Add("RateLimit-Reset", "60");
            return Task.FromResult(resp);
        });

        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        var ex = await Assert.ThrowsAsync<RateLimitException>(() => client.EnrichTransactionAsync("Uber", "GB"));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.True(ex.IsRateLimited());
        Assert.Equal(30, ex.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfterDelay);
        Assert.Equal(100, ex.RateLimitLimit);
        Assert.Equal(0, ex.RateLimitRemaining);
        Assert.Equal(60, ex.RateLimitReset);
        Assert.Equal("Rate limit exceeded.", ex.Message);
    }

    [Fact]
    public async Task Http429_RetryAfterAsHttpDate_ParsedSuccessfully()
    {
        string jsonError = @"{ ""title"": ""Too Many Requests"", ""detail"": ""Rate limit exceeded."" }";
        string futureDate = DateTimeOffset.UtcNow.AddSeconds(120).ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var resp = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent(jsonError, System.Text.Encoding.UTF8, "application/json")
            };
            resp.Headers.Add("Retry-After", futureDate);
            return Task.FromResult(resp);
        });

        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        var ex = await Assert.ThrowsAsync<RateLimitException>(() => client.EnrichTransactionAsync("Uber", "GB"));
        Assert.NotNull(ex.RetryAfter);
        Assert.True(ex.RetryAfter > 0);
        Assert.NotNull(ex.RetryAfterDelay);
        Assert.True(ex.RetryAfterDelay > TimeSpan.Zero);
    }

    [Fact]
    public void XyoClientConfig_WithDefaultHeader_CrlfInKeyOrValue_ThrowsArgumentException()
    {
        var config = new XyoClientConfig("xyo_test_token");
        var ex1 = Assert.Throws<ArgumentException>(() => config.WithDefaultHeader("X-Header\r\nInjected: evil", "value"));
        Assert.Contains("forbidden CRLF injection", ex1.Message);

        var ex2 = Assert.Throws<ArgumentException>(() => config.WithDefaultHeader("X-Header", "value\r\nInjected: evil"));
        Assert.Contains("forbidden CRLF injection", ex2.Message);
    }

    [Fact]
    public async Task EnrichTransactionAsync_CallerProvidedRequest_DoesNotMutateCountryCodeInPlace()
    {
        string jsonResponse = @"{ ""merchant"": ""Costa Coffee"", ""description"": ""Desc"", ""categories"": [], ""logo"": """", ""location"": """", ""address"": """" }";
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jsonResponse);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token_123"), httpClient);

        var request = new EnrichmentRequest("COSTA COFFEE", "gb");
        await client.EnrichTransactionAsync(request);

        Assert.Equal("gb", request.CountryCode);
    }

    [Fact]
    public async Task EnrichTransactionsAsync_CallerProvidedRequests_DoesNotMutateCountryCodeInPlace()
    {
        string jsonResponse = @"{ ""id"": ""job_123"", ""link"": ""https://download.xyo.financial/batches/123.tar.gz"" }";
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jsonResponse);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token_123"), httpClient);

        var item = new EnrichmentRequest("COSTA COFFEE", "gb");
        var batch = new List<EnrichmentRequest> { item };

        await client.EnrichTransactionsAsync(batch);

        Assert.Equal("gb", item.CountryCode);
    }

    private sealed class SlowContinuousStream : Stream
    {
        private readonly byte[] _data;
        private int _position;
        private readonly TimeSpan _chunkDelay;

        public SlowContinuousStream(byte[] data, TimeSpan chunkDelay)
        {
            _data = data;
            _chunkDelay = chunkDelay;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _data.Length)
            {
                return 0;
            }

            await Task.Delay(_chunkDelay, cancellationToken).ConfigureAwait(false);

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
    public async Task StreamEnrichmentCollectionAsync_SlowContinuousTransfer_NeverStalls_CompletesSuccessfully()
    {
        // Tests that a continuous stream delivering bytes steadily at intervals shorter than DownloadTimeout
        // (e.g. 20ms delay per chunk with a 100ms idle timeout, but total duration > 200ms) completes successfully
        // without false stall timeouts, proving that the idle timer resets on every byte/read.
        byte[] archiveData = TarGzOfRecords(3);
        var mockHandler = new MockHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new SlowContinuousStream(archiveData, TimeSpan.FromMilliseconds(20)))
            };
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(mockHandler);
        var config = new XyoClientConfig("xyo_test_token") { DownloadTimeout = TimeSpan.FromMilliseconds(100) };
        using var client = new XyoClient(config, httpClient);

        int count = 0;
        await foreach (var record in client.StreamEnrichmentCollectionAsync("https://api.xyo.financial/batches/1.tar.gz", CancellationToken.None))
        {
            count++;
        }

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task EnrichTransactionAsync_ExceedingUnarySizeCap_ThrowsXyoServerException()
    {
        // 2 MB of data returned with 200 OK (e.g. gateway HTML error page with Content-Type application/json)
        string oversized = "{\"data\":\"" + new string('A', 2 * 1024 * 1024) + "\"}";
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, oversized);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        var ex = await Assert.ThrowsAsync<XyoServerException>(() => client.EnrichTransactionAsync("Uber", "GB"));
        Assert.Contains("exceeded the maximum supported size", ex.Message);
    }

    [Fact]
    public async Task EnrichTransactionAsync_SchemaMismatch_AttachesBoundedRawResponseBody()
    {
        string invalidJson = "{\"merchant\": null, \"some_unexpected_field\": 12345}";
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, invalidJson);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        var ex = await Assert.ThrowsAsync<XyoServerException>(() => client.EnrichTransactionAsync("Uber", "GB"));
        Assert.Contains("does not conform to the enrichment schema", ex.Message);
        Assert.NotNull(ex.RawResponseBody);
        Assert.Equal(invalidJson, ex.RawResponseBody);
    }

    [Fact]
    public async Task EnrichTransactionsAsync_AlreadyNormalizedBatch_SendsItemsUnchanged()
    {
        // The batch path elides its defensive copy when an item's country code already equals its normalized
        // form. This asserts the observable contract of that optimization -- an already-uppercase batch is
        // transmitted verbatim -- rather than asserting how the BCL happens to implement ToUpperInvariant.
        string captured = string.Empty;
        var handler = new MockHttpMessageHandler(async (req, _) =>
        {
            captured = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    @"{ ""id"": ""job-1"", ""link"": ""https://api.xyo.financial/batches/1.tar.gz"" }",
                    System.Text.Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        var requests = new[]
        {
            new EnrichmentRequest("TESCO STORES 3428", "GB"),
            new EnrichmentRequest("UBER TRIP", "US")
        };

        await client.EnrichTransactionsAsync(requests);

        Assert.Contains(@"""countryCode"":""GB""", captured);
        Assert.Contains(@"""countryCode"":""US""", captured);
    }

    [Fact]
    public async Task EnrichTransactionsAsync_MixedCaseBatch_NormalizesWithoutMutatingCallerObjects()
    {
        string captured = string.Empty;
        var handler = new MockHttpMessageHandler(async (req, _) =>
        {
            captured = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    @"{ ""id"": ""job-1"", ""link"": ""https://api.xyo.financial/batches/1.tar.gz"" }",
                    System.Text.Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), httpClient);

        var lowercase = new EnrichmentRequest("TESCO STORES 3428", "gb");
        await client.EnrichTransactionsAsync(new[] { lowercase });

        Assert.Contains(@"""countryCode"":""GB""", captured);
        // The caller's own object is never mutated in place by normalization.
        Assert.Equal("gb", lowercase.CountryCode);
    }
}
