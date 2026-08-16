using System.Net;

namespace Xyo.Sdk.Exceptions;

/// <summary>
/// Exception thrown when the API returns an HTTP 4xx client-side error status code.
/// </summary>
public class XyoClientException : XyoException
{
    /// <summary>
    /// Gets the HTTP status code returned by the API server.
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// Gets the raw unparsed response body string, if available.
    /// </summary>
    public string? RawResponseBody { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="XyoClientException"/> class.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="message">The descriptive error message.</param>
    /// <param name="rawResponseBody">The unparsed response body content.</param>
    /// <param name="innerException">The inner exception that caused the error.</param>
    public XyoClientException(HttpStatusCode statusCode, string message, string? rawResponseBody = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        RawResponseBody = rawResponseBody;
    }

    /// <summary>
    /// Returns true if the client error is an authentication / authorization failure (401 Unauthorized or 403 Forbidden).
    /// </summary>
    public bool IsAuth() => StatusCode == HttpStatusCode.Unauthorized || StatusCode == HttpStatusCode.Forbidden;

    /// <summary>
    /// Returns true if the target resource was not found (404 Not Found).
    /// </summary>
    public bool IsNotFound() => StatusCode == HttpStatusCode.NotFound;

    /// <summary>
    /// Returns true if the request was rejected due to rate limiting (429 Too Many Requests).
    /// </summary>
    public bool IsRateLimited() => (int)StatusCode == 429;
}
