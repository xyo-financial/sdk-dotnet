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

    [Fact]
    public void FromJson_UnparseableBody_FlattensControlCharactersInMessage()
    {
        // The fallback fires when the body is not parseable JSON despite a JSON content type, which is
        // precisely the malformed-or-hostile upstream case (a proxy HTML error page, a truncated response).
        // Before this was shared with SafeSummary, that path substring'd the payload straight into
        // Exception.Message, so ESC, NUL and U+2028 all reached the log intact (CWE-117).
        const char esc = '\u001b';
        const char lineSeparator = '\u2028';
        string hostile = $"not json{esc}[31mANSI\n2026-08-30 INFO forged line{lineSeparator}sep\0nul";

        var ex = XyoProblemDetailsException.FromJson(HttpStatusCode.BadRequest, hostile);

        Assert.DoesNotContain(esc, ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain(lineSeparator, ex.Message);
        Assert.DoesNotContain('\0', ex.Message);

        // Full fidelity is still available for callers that opt in.
        Assert.Equal(hostile, ex.RawResponseBody);
    }

    [Fact]
    public void FromJson_UnparseableBodyStraddlingTheClamp_DoesNotEmitLoneSurrogate()
    {
        // A surrogate pair positioned so a naive Substring(0, 512) would split it.
        string hostile = new string('B', 511) + "\U0001F600" + "tail";

        var ex = XyoProblemDetailsException.FromJson(HttpStatusCode.BadRequest, hostile);

        string trimmed = ex.Message.TrimEnd('\u2026');
        Assert.False(char.IsSurrogate(trimmed[^1]), "clamped message must not end in a lone surrogate");
    }

    [Fact]
    public void FromJson_TitleOnly_UsesTitleAsTheMessage()
    {
        // RFC 7807 makes every member optional. With no "detail", the message falls back to "title".
        var ex = XyoProblemDetailsException.FromJson(HttpStatusCode.BadRequest, @"{ ""title"": ""Validation failed"" }");

        Assert.Equal("Validation failed", ex.Message);
        Assert.Equal("Validation failed", ex.Title);
        Assert.Null(ex.Detail);
    }

    [Fact]
    public void FromJson_NeitherDetailNorTitle_FallsBackToStatusLine()
    {
        var ex = XyoProblemDetailsException.FromJson(HttpStatusCode.BadRequest, @"{ ""type"": ""about:blank"" }");

        Assert.Contains("[HTTP 400]", ex.Message);
        Assert.Equal("about:blank", ex.Type);
        Assert.Null(ex.Title);
        Assert.Null(ex.Detail);
    }

    [Fact]
    public void FromJson_ErrorsAsScalarStrings_AreWrappedIntoSingleElementArrays()
    {
        // ASP.NET Core emits arrays, but other stacks emit a bare string per field. Both must land in the
        // same shape so a caller reading Errors does not have to branch on the server's dialect.
        string payload = @"{ ""title"": ""Invalid"", ""errors"": { ""countryCode"": ""must be ISO 3166-1 alpha-2"" } }";

        var ex = XyoProblemDetailsException.FromJson(HttpStatusCode.BadRequest, payload);

        Assert.NotNull(ex.Errors);
        Assert.Equal(new[] { "must be ISO 3166-1 alpha-2" }, ex.Errors!["countryCode"]);
    }

    [Fact]
    public void FromJson_ErrorsWithUnsupportedValueKind_IsIgnoredRatherThanThrowing()
    {
        // A numeric or object value under "errors" is neither an array nor a string. It must be skipped so
        // one unexpected field cannot cost the caller the rest of a parseable problem document.
        string payload = @"{ ""title"": ""Invalid"", ""errors"": { ""retries"": 3, ""countryCode"": [""required""] } }";

        var ex = XyoProblemDetailsException.FromJson(HttpStatusCode.BadRequest, payload);

        Assert.NotNull(ex.Errors);
        Assert.False(ex.Errors!.ContainsKey("retries"));
        Assert.Equal(new[] { "required" }, ex.Errors["countryCode"]);
    }

    [Fact]
    public void FromJson_ErrorsArrayWithNonStringElements_KeepsOnlyTheStrings()
    {
        string payload = @"{ ""title"": ""Invalid"", ""errors"": { ""content"": [""too long"", 42, null] } }";

        var ex = XyoProblemDetailsException.FromJson(HttpStatusCode.BadRequest, payload);

        Assert.Equal(new[] { "too long" }, ex.Errors!["content"]);
    }
}
