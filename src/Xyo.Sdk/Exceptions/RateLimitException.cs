using System;
using System.Collections.Generic;
using System.Net;

namespace Xyo.Sdk.Exceptions;

/// <summary>
/// Exception thrown when the API server returns an HTTP 429 Too Many Requests status code.
/// </summary>
public class RateLimitException : XyoProblemDetailsException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitException"/> class.
    /// </summary>
    /// <param name="statusCode">The HTTP status code (typically 429 Too Many Requests).</param>
    /// <param name="message">The descriptive error message.</param>
    /// <param name="retryAfter">The recommended retry wait duration in seconds, if specified by the Retry-After header.</param>
    /// <param name="rateLimitLimit">The maximum number of requests allowed in the rate limit window, if specified by RateLimit-Limit.</param>
    /// <param name="rateLimitRemaining">The remaining number of requests allowed in the rate limit window, if specified by RateLimit-Remaining.</param>
    /// <param name="rateLimitReset">The rate limit reset duration or timestamp in seconds, if specified by RateLimit-Reset.</param>
    /// <param name="type">Optional URI reference identifying the problem type.</param>
    /// <param name="title">Optional short human-readable summary of the problem type.</param>
    /// <param name="status">Optional HTTP status code from origin server.</param>
    /// <param name="detail">Optional human-readable explanation specific to this occurrence.</param>
    /// <param name="instance">Optional URI reference identifying the specific occurrence.</param>
    /// <param name="errors">Optional structured field-level or category-level error details dictionary.</param>
    /// <param name="rawResponseBody">The raw unparsed response body string, if available.</param>
    /// <param name="innerException">The inner exception that caused the error.</param>
    public RateLimitException(
        HttpStatusCode statusCode,
        string message,
        int? retryAfter = null,
        int? rateLimitLimit = null,
        int? rateLimitRemaining = null,
        int? rateLimitReset = null,
        string? type = null,
        string? title = null,
        int? status = null,
        string? detail = null,
        string? instance = null,
        IReadOnlyDictionary<string, string[]>? errors = null,
        string? rawResponseBody = null,
        Exception? innerException = null)
        : base(statusCode, message, type, title, status, detail, instance, errors, rawResponseBody, innerException)
    {
        RetryAfter = retryAfter;
        RateLimitLimit = rateLimitLimit;
        RateLimitRemaining = rateLimitRemaining;
        RateLimitReset = rateLimitReset;
    }
}
