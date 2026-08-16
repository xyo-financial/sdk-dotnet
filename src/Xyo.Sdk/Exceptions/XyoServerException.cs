using System.Net;

namespace Xyo.Sdk.Exceptions;

/// <summary>
/// Exception thrown when the API returns an HTTP 5xx server-side error status code.
/// </summary>
public class XyoServerException : XyoException
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
    /// Initializes a new instance of the <see cref="XyoServerException"/> class.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="message">The descriptive error message.</param>
    /// <param name="rawResponseBody">The unparsed response body content.</param>
    /// <param name="innerException">The inner exception that caused the error.</param>
    public XyoServerException(HttpStatusCode statusCode, string message, string? rawResponseBody = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        RawResponseBody = rawResponseBody;
    }

    /// <summary>
    /// Returns true if the server error is transient and safe to retry with exponential backoff.
    /// </summary>
    public bool IsRetryable()
    {
        return StatusCode is HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }
}
