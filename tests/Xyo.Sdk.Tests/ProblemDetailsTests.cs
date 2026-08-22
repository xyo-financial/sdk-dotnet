using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;
using Xyo.Sdk.Client;
using Xyo.Sdk.Exceptions;

namespace Xyo.Sdk.Tests;

public class ProblemDetailsTests
{
    [Fact]
    public async Task Http400_WithRfc7807Payload_ThrowsXyoProblemDetailsException()
    {
        string rfc7807Json = @"
        {
            ""type"": ""https://api.xyo.financial/errors/validation-error"",
            ""title"": ""Invalid Request Parameters"",
            ""status"": 400,
            ""detail"": ""The country code provided is not supported."",
            ""instance"": ""/errors/req_778899"",
            ""errors"": {
                ""countryCode"": [""Must be an ISO 3166-1 alpha-2 format""]
            }
        }";

        var handler = new MockHttpMessageHandler(HttpStatusCode.BadRequest, rfc7807Json);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_token"), httpClient);

        var ex = await Assert.ThrowsAsync<XyoProblemDetailsException>(() => client.EnrichTransactionAsync("COSTA", "GB"));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("https://api.xyo.financial/errors/validation-error", ex.Type);
        Assert.Equal("Invalid Request Parameters", ex.Title);
        Assert.Equal(400, ex.Status);
        Assert.Equal("The country code provided is not supported.", ex.Detail);
        Assert.NotNull(ex.Errors);
        Assert.True(ex.Errors.ContainsKey("countryCode"));
        Assert.Equal("Must be an ISO 3166-1 alpha-2 format", ex.Errors["countryCode"][0]);
    }

    [Fact]
    public async Task Http401_ThrowsXyoClientException_WithIsAuthTrue()
    {
        string errorJson = @"{ ""title"": ""Unauthorized"", ""status"": 401, ""detail"": ""Invalid or expired API token."" }";
        var handler = new MockHttpMessageHandler(HttpStatusCode.Unauthorized, errorJson);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_token"), httpClient);

        var ex = await Assert.ThrowsAsync<XyoProblemDetailsException>(() => client.EnrichTransactionAsync("COSTA", "GB"));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.True(ex.IsAuth());
        Assert.False(ex.IsRateLimited());
    }

    [Fact]
    public async Task Http429_ThrowsXyoClientException_WithIsRateLimitedTrue()
    {
        string errorJson = @"{ ""title"": ""Rate Limited"", ""status"": 429, ""detail"": ""Too many requests."" }";
        var handler = new MockHttpMessageHandler((HttpStatusCode)429, errorJson);
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_token"), httpClient);

        var ex = await Assert.ThrowsAsync<RateLimitException>(() => client.EnrichTransactionAsync("COSTA", "GB"));

        Assert.Equal((HttpStatusCode)429, ex.StatusCode);
        Assert.True(ex.IsRateLimited());
    }

    [Fact]
    public async Task Http500_ThrowsXyoServerException_WithIsRetryableTrue()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.InternalServerError, "Internal Server Error");
        using var httpClient = new HttpClient(handler);
        using var client = new XyoClient(new XyoClientConfig("xyo_token"), httpClient);

        var ex = await Assert.ThrowsAsync<XyoServerException>(() => client.EnrichTransactionAsync("COSTA", "GB"));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.True(ex.IsRetryable());
    }
}
