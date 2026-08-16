using System;
using System.Collections.Generic;
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
    public async Task EnrichTransactionAsync_EmptyContent_ThrowsXyoClientException()
    {
        using var client = new XyoClient("xyo_test_token_123");

        var ex = await Assert.ThrowsAsync<XyoClientException>(() => client.EnrichTransactionAsync("", "GB"));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("cannot be null, empty", ex.Message);
    }

    [Fact]
    public async Task EnrichTransactionAsync_ContentExceeding128Chars_ThrowsXyoClientException()
    {
        using var client = new XyoClient("xyo_test_token_123");
        string longContent = new string('A', 129);

        var ex = await Assert.ThrowsAsync<XyoClientException>(() => client.EnrichTransactionAsync(longContent, "GB"));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("exceeds maximum length of 128", ex.Message);
    }

    [Theory]
    [InlineData("G")]
    [InlineData("GBR")]
    [InlineData("12")]
    [InlineData("G-")]
    public async Task EnrichTransactionAsync_InvalidCountryCode_ThrowsXyoClientException(string invalidCountryCode)
    {
        using var client = new XyoClient("xyo_test_token_123");

        var ex = await Assert.ThrowsAsync<XyoClientException>(() => client.EnrichTransactionAsync("Valid Content", invalidCountryCode));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
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
    public async Task EnrichTransactionsAsync_EmptyBatch_ThrowsXyoClientException()
    {
        using var client = new XyoClient("xyo_test_token_123");

        var ex = await Assert.ThrowsAsync<XyoClientException>(() => client.EnrichTransactionsAsync(new List<EnrichmentRequest>()));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("cannot be empty", ex.Message);
    }

    [Fact]
    public async Task EnrichTransactionsAsync_CrlfInApiUser_ThrowsXyoClientException()
    {
        using var client = new XyoClient("xyo_test_token_123");
        var batch = new List<EnrichmentRequest> { new("COSTA", "GB") };

        var ex = await Assert.ThrowsAsync<XyoClientException>(() => client.EnrichTransactionsAsync(batch, apiUser: "user\r\nInjected-Header: evil"));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("forbidden CRLF injection", ex.Message);
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
}
