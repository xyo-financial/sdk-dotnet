using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Xyo.Sdk.Tests;

public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public List<HttpRequestMessage> CapturedRequests { get; } = new();

    public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public MockHttpMessageHandler(HttpResponseMessage staticResponse)
    {
        _handler = (_, _) => Task.FromResult(staticResponse);
    }

    public MockHttpMessageHandler(HttpStatusCode statusCode, string jsonContent)
    {
        _handler = (_, _) =>
        {
            var resp = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CapturedRequests.Add(request);
        return await _handler(request, cancellationToken).ConfigureAwait(false);
    }
}
