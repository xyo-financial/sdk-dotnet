using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xyo.Generated.Model;
using Xyo.Sdk.Client;
using Xyo.Sdk.Exceptions;
using Xyo.Sdk.Extensions;
using Xyo.Sdk.Security;

namespace Xyo.Sdk.Tests;

/// <summary>
/// Pins the public surface a consumer builds against: argument guards, the Guid-typed tracing overloads, and
/// the exception predicates that retry policies branch on. These are the parts most likely to be depended on
/// without being exercised by the SDK's own happy paths.
/// </summary>
public class ApiContractTests
{
    private const string EnrichmentJson =
        @"{ ""merchant"": ""Costa"", ""description"": ""D"", ""categories"": [""General""], ""logo"": ""L"", ""location"": ""London"", ""address"": ""1 High St"" }";

    private const string BatchJson =
        @"{ ""id"": ""job-1"", ""link"": ""https://download.xyo.financial/batches/1.tar.gz"" }";

    private const string StatusJson = @"{ ""status"": ""READY"" }";

    private static XyoClient ClientReturning(string json, out MockHttpMessageHandler handler)
    {
        handler = new MockHttpMessageHandler(HttpStatusCode.OK, json);
        return new XyoClient(new XyoClientConfig("xyo_test_token"), new HttpClient(handler));
    }

    // ---------- Argument guards ----------

    [Fact]
    public void Constructor_NullConfig_ThrowsArgumentNullException() =>
        Assert.Throws<ArgumentNullException>(() => new XyoClient((XyoClientConfig)null!));

    [Fact]
    public async Task EnrichTransactionAsync_NullRequest_ThrowsArgumentNullException()
    {
        using var client = ClientReturning(EnrichmentJson, out _);
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.EnrichTransactionAsync((EnrichmentRequest)null!));
    }

    [Fact]
    public async Task EnrichTransactionsAsync_NullCollection_ThrowsArgumentNullException()
    {
        using var client = ClientReturning(BatchJson, out _);
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.EnrichTransactionsAsync(null!));
    }

    [Fact]
    public async Task EnrichTransactionsAsync_LazilyEvaluatedSequence_IsMaterialisedOnce()
    {
        // A sequence that is not already an IReadOnlyList takes the ToList() path. Deferred LINQ is the
        // normal way callers hand over a batch, so it must not be enumerated twice or rejected.
        using var client = ClientReturning(BatchJson, out var handler);
        IEnumerable<EnrichmentRequest> lazy = new[]
        {
            new EnrichmentRequest("UBER TRIP", "GB"),
            new EnrichmentRequest("TESCO", "GB")
        }.Where(_ => true);

        var result = await client.EnrichTransactionsAsync(lazy);

        Assert.Equal("job-1", result.Id);
        Assert.Single(handler.CapturedRequests);
    }

    // ---------- Guid-typed tracing overloads ----------

    private static readonly Guid CorrelationId = Guid.Parse("0af76519-16cd-43dd-8448-eb211c80319c");
    private const string Traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    [Fact]
    public async Task EnrichTransactionAsync_GuidCorrelationOverload_SendsFormattedHeader()
    {
        using var client = ClientReturning(EnrichmentJson, out var handler);

        await client.EnrichTransactionAsync("UBER TRIP", "GB", CorrelationId, Traceparent);

        var sent = handler.CapturedRequests[0];
        Assert.Equal(CorrelationId.ToString("D"), sent.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal(Traceparent, sent.Headers.GetValues("traceparent").Single());
    }

    [Fact]
    public async Task EnrichTransactionAsync_RequestWithGuidCorrelationOverload_SendsFormattedHeader()
    {
        using var client = ClientReturning(EnrichmentJson, out var handler);

        await client.EnrichTransactionAsync(new EnrichmentRequest("UBER TRIP", "GB"), CorrelationId, Traceparent);

        Assert.Equal(CorrelationId.ToString("D"), handler.CapturedRequests[0].Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task EnrichTransactionsAsync_GuidCorrelationOverload_SendsFormattedHeader()
    {
        using var client = ClientReturning(BatchJson, out var handler);

        await client.EnrichTransactionsAsync(
            new[] { new EnrichmentRequest("UBER TRIP", "GB") }, apiUser: null, correlationId: CorrelationId, traceparent: Traceparent);

        Assert.Equal(CorrelationId.ToString("D"), handler.CapturedRequests[0].Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task GetEnrichmentStatusAsync_GuidCorrelationOverload_SendsFormattedHeader()
    {
        using var client = ClientReturning(StatusJson, out var handler);

        await client.GetEnrichmentStatusAsync("job-1", apiUser: null, correlationId: CorrelationId, traceparent: Traceparent);

        Assert.Equal(CorrelationId.ToString("D"), handler.CapturedRequests[0].Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task GetEnrichmentStatusAsync_SendsIdAsPathParameter()
    {
        // The specification declares GET /v1/ai/finance/enrichment/status/{id}. Nothing here
        // asserted the request path before, which is why this SDK called an undeclared
        // /transaction/collection/status?id= route unnoticed. See xyo-financial/specs#15.
        using var client = ClientReturning(StatusJson, out var handler);

        await client.GetEnrichmentStatusAsync("72c037df-d0d3-43ee-9470-323ff35a2e50");

        var requestUri = handler.CapturedRequests[0].RequestUri!;
        Assert.Equal("/v1/ai/finance/enrichment/status/72c037df-d0d3-43ee-9470-323ff35a2e50", requestUri.AbsolutePath);
        Assert.Empty(requestUri.Query);
    }

    [Fact]
    public async Task GetEnrichmentStatusAsync_EscapesSlashesInIdSoItCannotInjectPathSegments()
    {
        using var client = ClientReturning(StatusJson, out var handler);

        await client.GetEnrichmentStatusAsync("abc/../../admin");

        // The escaping must survive into the request, keeping the identifier one segment.
        Assert.Contains("abc%2F..%2F..%2Fadmin", handler.CapturedRequests[0].RequestUri!.OriginalString);
    }

    [Fact]
    public async Task GetEnrichmentStatusAsync_NullCorrelationGuid_SendsNoCorrelationHeader()
    {
        using var client = ClientReturning(StatusJson, out var handler);

        await client.GetEnrichmentStatusAsync("job-1", apiUser: null, correlationId: (Guid?)null);

        Assert.False(handler.CapturedRequests[0].Headers.NonValidated.Contains("X-Correlation-ID"));
    }

    // ---------- Configuration guards ----------

    [Fact]
    public void TrustedDownloadHosts_Null_ThrowsArgumentNullException() =>
        Assert.Throws<ArgumentNullException>(() => new XyoClientConfig("key") { TrustedDownloadHosts = null! });

    [Fact]
    public void AddTrustedDownloadHost_Null_ThrowsArgumentNullException() =>
        Assert.Throws<ArgumentNullException>(() => new XyoClientConfig("key").AddTrustedDownloadHost(null!));

    [Fact]
    public void WithCorrelationId_Crlf_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new XyoClientConfig("key").WithCorrelationId("id\r\nInjected: 1"));
        Assert.Contains("CRLF", ex.Message);
    }

    [Fact]
    public void WithTraceparent_Crlf_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new XyoClientConfig("key").WithTraceparent("00-a\r\nInjected: 1"));
        Assert.Contains("CRLF", ex.Message);
    }

    [Fact]
    public void WithDefaultHeader_EmptyKey_ThrowsArgumentException() =>
        Assert.Throws<ArgumentException>(() => new XyoClientConfig("key").WithDefaultHeader("  ", "value"));

    [Fact]
    public void ToString_WithNoApiKey_ReportsDynamicOrNone() =>
        Assert.Contains("(Dynamic/None)", new XyoClientConfig().ToString());

    [Fact]
    public async Task ResolveTokenAsync_SupplierReturningBlank_FallsBackToStaticKey()
    {
        // A supplier that yields nothing must not blank out a configured static key; the static key is the
        // documented fallback, and silently sending an empty Bearer token would fail as a 401 instead.
        var config = new XyoClientConfig("static_key").WithTokenSupplier(() => "   ");

        Assert.Equal("static_key", await config.ResolveTokenAsync());
    }

    [Fact]
    public async Task ResolveTokenAsync_NoKeyAndNoSupplier_ThrowsInvalidOperationException() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => new XyoClientConfig().ResolveTokenAsync());

    // ---------- Download policy guards ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateDownloadUrl_BlankUrl_ThrowsXyoClientException(string url)
    {
        var ex = Assert.Throws<XyoClientException>(() => new DownloadSecurityPolicy().ValidateDownloadUrl(url));
        Assert.Contains("cannot be null, empty", ex.Message);
    }

    [Fact]
    public void ValidateDownloadUrl_UnparseableUri_ThrowsXyoClientException()
    {
        var ex = Assert.Throws<XyoClientException>(() => new DownloadSecurityPolicy().ValidateDownloadUrl("not a valid uri at all"));
        Assert.Contains("not a valid absolute URI", ex.Message);
    }

    [Fact]
    public void ValidateDownloadUrl_RootRelativePath_IsRejected()
    {
        // Rejected, but note which rule catches it: on Unix a leading slash parses as an absolute file://
        // URI, so this trips the scheme allowlist rather than the absolute-URI check. On Windows the same
        // input fails to parse and trips the other branch. Either way the request never leaves the process,
        // which is what the assertion pins; asserting the specific message here would make the test
        // platform-dependent.
        Assert.Throws<XyoClientException>(() => new DownloadSecurityPolicy().ValidateDownloadUrl("/batches/1.tar.gz"));
    }

    [Fact]
    public void IsExternalStorageHost_ProductionApiHost_IsNeverExternal()
    {
        // Holds even when the policy was configured against a different base URL, so the production host is
        // never treated as a third party that should have its credentials stripped.
        var policy = new DownloadSecurityPolicy("https://sandbox.xyo.financial");

        Assert.False(policy.IsExternalStorageHost("api.xyo.financial"));
        Assert.False(policy.IsExternalStorageHost("sandbox.xyo.financial"));
        Assert.True(policy.IsExternalStorageHost("xyo-financial.s3.amazonaws.com"));
    }

    // ---------- Dependency injection guards ----------

    [Fact]
    public void AddXyoClient_NullServices_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddXyoClient(_ => { }));
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddXyoClient(new XyoClientConfig("key")));
    }

    [Fact]
    public void AddXyoClient_NullArguments_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddXyoClient((Action<XyoClientOptions>)null!));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddXyoClient((XyoClientConfig)null!));
    }

    // ---------- Exception predicates that retry policies branch on ----------

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.NotImplemented, false)]
    [InlineData(HttpStatusCode.HttpVersionNotSupported, false)]
    public void XyoServerException_IsRetryable_MatchesTransientStatusCodes(HttpStatusCode code, bool expected) =>
        Assert.Equal(expected, new XyoServerException(code, "boom").IsRetryable());

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public void XyoClientException_IsAuth_IdentifiesAuthFailures(HttpStatusCode code, bool expected) =>
        Assert.Equal(expected, new XyoClientException(code, "boom").IsAuth());

    [Theory]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public void XyoClientException_IsNotFound_IdentifiesMissingResources(HttpStatusCode code, bool expected) =>
        Assert.Equal(expected, new XyoClientException(code, "boom").IsNotFound());

    [Theory]
    [InlineData(429, true)]
    [InlineData(400, false)]
    public void XyoClientException_IsRateLimited_IdentifiesThrottling(int code, bool expected) =>
        Assert.Equal(expected, new XyoClientException((HttpStatusCode)code, "boom").IsRateLimited());

    [Fact]
    public void RateLimitException_RetryAfterDelay_ProjectsSecondsOrNull()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), new RateLimitException(HttpStatusCode.TooManyRequests, "slow down", retryAfter: 30).RetryAfterDelay);
        Assert.Null(new RateLimitException(HttpStatusCode.TooManyRequests, "slow down").RetryAfterDelay);
    }

    [Fact]
    public void XyoNetworkException_IsAlwaysRetryable() =>
        Assert.True(new XyoNetworkException("connection reset").IsRetryable);

    [Fact]
    public void XyoException_ConstructorOverloads_PreserveMessageAndInnerException()
    {
        var inner = new InvalidOperationException("root cause");

        Assert.Equal("boom", new XyoException("boom").Message);
        Assert.Same(inner, new XyoException("boom", inner).InnerException);
        Assert.NotNull(new XyoException().Message);
    }

    // ---------- Buffered download and archive plumbing ----------

    private static byte[] TarGzOf(params string[] records)
    {
        using var tarMs = new MemoryStream();
        using (var tarWriter = new System.Formats.Tar.TarWriter(tarMs, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
        {
            for (int i = 0; i < records.Length; i++)
            {
                tarWriter.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, $"{i:000}.json")
                {
                    DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(records[i]))
                });
            }
        }
        tarMs.Position = 0;
        using var gzMs = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(gzMs, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            tarMs.CopyTo(gz);
        }
        return gzMs.ToArray();
    }

    [Fact]
    public async Task DownloadEnrichmentCollectionAsync_BuffersEveryRecord()
    {
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TarGzOf(EnrichmentJson, EnrichmentJson)) }));
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), new HttpClient(handler));

        var results = await client.DownloadEnrichmentCollectionAsync("https://api.xyo.financial/batches/1.tar.gz");

        Assert.Equal(2, results.Count);
        Assert.Equal("Costa", results[0].Merchant);
    }

    [Fact]
    public async Task StreamEnrichmentCollectionAsync_RelativeRedirectLocation_IsResolvedAgainstTheCurrentHop()
    {
        int calls = 0;
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            if (++calls == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("/batches/final.tar.gz", UriKind.Relative);
                return Task.FromResult(redirect);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TarGzOf(EnrichmentJson)) });
        });
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), new HttpClient(handler));

        var results = await client.DownloadEnrichmentCollectionAsync("https://api.xyo.financial/batches/1.tar.gz");

        Assert.Single(results);
        Assert.Equal("https://api.xyo.financial/batches/final.tar.gz", handler.CapturedRequests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task StreamEnrichmentCollectionAsync_InfiniteTotalDuration_DisablesTheBound()
    {
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TarGzOf(EnrichmentJson)) }));
        var config = new XyoClientConfig("xyo_test_token") { MaxTotalDownloadDuration = Timeout.InfiniteTimeSpan };
        using var client = new XyoClient(config, new HttpClient(handler));

        var results = await client.DownloadEnrichmentCollectionAsync("https://api.xyo.financial/batches/1.tar.gz");

        Assert.Single(results);
    }

    // ---------- Request validation reached through the public surface ----------

    [Fact]
    public async Task GetEnrichmentStatusAsync_BlankId_ThrowsArgumentException()
    {
        using var client = ClientReturning(StatusJson, out _);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetEnrichmentStatusAsync("   "));
    }

    [Fact]
    public async Task GetEnrichmentStatusAsync_ApiUser_IsSentAsTenantHeader()
    {
        using var client = ClientReturning(StatusJson, out var handler);

        await client.GetEnrichmentStatusAsync("job-1", apiUser: "tenant_alpha");

        Assert.Equal("tenant_alpha", handler.CapturedRequests[0].Headers.GetValues("x-api-user").Single());
    }

    [Fact]
    public async Task EnrichTransactionsAsync_NullItemInBatch_NamesTheOffendingIndex()
    {
        using var client = ClientReturning(BatchJson, out _);
        var batch = new[] { new EnrichmentRequest("UBER", "GB"), null! };

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => client.EnrichTransactionsAsync(batch));
        Assert.Contains("index 1", ex.Message);
    }

    [Fact]
    public async Task EnrichTransactionAsync_NullCountryCode_ThrowsArgumentException()
    {
        using var client = ClientReturning(EnrichmentJson, out _);
        await Assert.ThrowsAsync<ArgumentException>(() => client.EnrichTransactionAsync("UBER TRIP", null!));
    }

    [Fact]
    public async Task EnrichTransactionAsync_MalformedTraceparent_IsRejectedBeforeSending()
    {
        using var client = ClientReturning(EnrichmentJson, out var handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.EnrichTransactionAsync("UBER TRIP", "GB", (string?)null, "not-a-traceparent"));

        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task EnrichTransactionAsync_JsonNullBody_ThrowsEmptyPayloadException()
    {
        using var client = ClientReturning("null", out _);

        var ex = await Assert.ThrowsAsync<XyoServerException>(() => client.EnrichTransactionAsync("UBER TRIP", "GB"));
        Assert.Contains("empty payload", ex.Message);
    }

    [Fact]
    public async Task EnsureSuccessResponse_EmptyErrorBody_UsesTheFallbackMessage()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(string.Empty) }));
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<XyoServerException>(() => client.EnrichTransactionAsync("UBER TRIP", "GB"));
        Assert.Contains("Upstream server error", ex.Message);
    }

    [Fact]
    public async Task RateLimit_RetryAfterHttpDateInThePast_ClampsToZero()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("slow down", System.Text.Encoding.UTF8, "text/plain") };
            response.Headers.TryAddWithoutValidation("Retry-After", DateTimeOffset.UtcNow.AddMinutes(-5).ToString("R"));
            return Task.FromResult(response);
        });
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<RateLimitException>(() => client.EnrichTransactionAsync("UBER TRIP", "GB"));
        Assert.Equal(0, ex.RetryAfter);
    }

    [Fact]
    public async Task RateLimit_BlankRateLimitHeaders_AreIgnored()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("slow down", System.Text.Encoding.UTF8, "text/plain") };
            response.Headers.TryAddWithoutValidation("Retry-After", "   ");
            return Task.FromResult(response);
        });
        using var client = new XyoClient(new XyoClientConfig("xyo_test_token"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<RateLimitException>(() => client.EnrichTransactionAsync("UBER TRIP", "GB"));
        Assert.Null(ex.RetryAfter);
    }
}
