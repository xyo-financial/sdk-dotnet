using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xyo.Generated.Model;

namespace Xyo.Sdk.Client;

/// <summary>
/// Primary interface for interacting with the XYO Financial AI Transaction Enrichment API.
/// </summary>
public interface IXyoClient : IDisposable
{
    /// <summary>
    /// Synchronously enriches a single bank transaction description with merchant, category, logo, and address metadata.
    /// </summary>
    /// <param name="content">Raw payment narrative (maximum 128 characters).</param>
    /// <param name="countryCode">ISO 3166-1 alpha-2 two-character country code (e.g. "GB", "US").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Strongly-typed enriched merchant profile.</returns>
    Task<EnrichmentResponse> EnrichTransactionAsync(string content, string countryCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously enriches a single bank transaction description with distributed tracing headers.
    /// </summary>
    /// <param name="content">Raw payment narrative (maximum 128 characters).</param>
    /// <param name="countryCode">ISO 3166-1 alpha-2 two-character country code (e.g. "GB", "US").</param>
    /// <param name="correlationId">Optional request correlation identifier (X-Correlation-ID).</param>
    /// <param name="traceparent">Optional W3C traceparent header (traceparent).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Strongly-typed enriched merchant profile.</returns>
    Task<EnrichmentResponse> EnrichTransactionAsync(string content, string countryCode, string? correlationId, string? traceparent = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously enriches a single bank transaction description with distributed tracing headers.
    /// </summary>
    /// <param name="content">Raw payment narrative (maximum 128 characters).</param>
    /// <param name="countryCode">ISO 3166-1 alpha-2 two-character country code (e.g. "GB", "US").</param>
    /// <param name="correlationId">Optional request correlation identifier (X-Correlation-ID) as a <see cref="Guid"/>.</param>
    /// <param name="traceparent">Optional W3C traceparent header (traceparent).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Strongly-typed enriched merchant profile.</returns>
    Task<EnrichmentResponse> EnrichTransactionAsync(string content, string countryCode, Guid? correlationId, string? traceparent = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously enriches a single bank transaction using a structured <see cref="EnrichmentRequest"/>.
    /// </summary>
    /// <param name="request">Transaction request payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Strongly-typed enriched merchant profile.</returns>
    Task<EnrichmentResponse> EnrichTransactionAsync(EnrichmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously enriches a single bank transaction using a structured <see cref="EnrichmentRequest"/> with distributed tracing headers.
    /// </summary>
    /// <param name="request">Transaction request payload.</param>
    /// <param name="correlationId">Optional request correlation identifier (X-Correlation-ID).</param>
    /// <param name="traceparent">Optional W3C traceparent header (traceparent).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Strongly-typed enriched merchant profile.</returns>
    Task<EnrichmentResponse> EnrichTransactionAsync(EnrichmentRequest request, string? correlationId, string? traceparent = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously enriches a single bank transaction using a structured <see cref="EnrichmentRequest"/> with distributed tracing headers.
    /// </summary>
    /// <param name="request">Transaction request payload.</param>
    /// <param name="correlationId">Optional request correlation identifier (X-Correlation-ID) as a <see cref="Guid"/>.</param>
    /// <param name="traceparent">Optional W3C traceparent header (traceparent).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Strongly-typed enriched merchant profile.</returns>
    Task<EnrichmentResponse> EnrichTransactionAsync(EnrichmentRequest request, Guid? correlationId, string? traceparent = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits an asynchronous batch collection of transactions for high-throughput enrichment.
    /// </summary>
    /// <param name="requests">List of transaction payloads to enrich.</param>
    /// <param name="apiUser">Optional tenant or application user identifier for audit routing (x-api-user).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Job identifier and archive download link.</returns>
    Task<EnrichTransactionCollectionResponse> EnrichTransactionsAsync(IEnumerable<EnrichmentRequest> requests, string? apiUser = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits an asynchronous batch collection of transactions for high-throughput enrichment with distributed tracing headers.
    /// </summary>
    /// <param name="requests">List of transaction payloads to enrich.</param>
    /// <param name="apiUser">Optional tenant or application user identifier for audit routing (x-api-user).</param>
    /// <param name="correlationId">Optional request correlation identifier (X-Correlation-ID).</param>
    /// <param name="traceparent">Optional W3C traceparent header (traceparent).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Job identifier and archive download link.</returns>
    Task<EnrichTransactionCollectionResponse> EnrichTransactionsAsync(IEnumerable<EnrichmentRequest> requests, string? apiUser, string? correlationId, string? traceparent = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits an asynchronous batch collection of transactions for high-throughput enrichment with distributed tracing headers.
    /// </summary>
    /// <param name="requests">List of transaction payloads to enrich.</param>
    /// <param name="apiUser">Optional tenant or application user identifier for audit routing (x-api-user).</param>
    /// <param name="correlationId">Optional request correlation identifier (X-Correlation-ID) as a <see cref="Guid"/>.</param>
    /// <param name="traceparent">Optional W3C traceparent header (traceparent).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Job identifier and archive download link.</returns>
    Task<EnrichTransactionCollectionResponse> EnrichTransactionsAsync(IEnumerable<EnrichmentRequest> requests, string? apiUser, Guid? correlationId, string? traceparent = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the processing lifecycle status of an asynchronous bulk enrichment batch job.
    /// </summary>
    /// <param name="id">The unique enrichment job identifier.</param>
    /// <param name="apiUser">Optional tenant identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current job lifecycle status (READY, PENDING, FAILED).</returns>
    Task<EnrichmentCollectionStatusResponse> GetEnrichmentStatusAsync(string id, string? apiUser = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the processing lifecycle status of an asynchronous bulk enrichment batch job with distributed tracing headers.
    /// </summary>
    /// <param name="id">The unique enrichment job identifier.</param>
    /// <param name="apiUser">Optional tenant identifier.</param>
    /// <param name="correlationId">Optional request correlation identifier (X-Correlation-ID).</param>
    /// <param name="traceparent">Optional W3C traceparent header (traceparent).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current job lifecycle status (READY, PENDING, FAILED).</returns>
    Task<EnrichmentCollectionStatusResponse> GetEnrichmentStatusAsync(string id, string? apiUser, string? correlationId, string? traceparent = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the processing lifecycle status of an asynchronous bulk enrichment batch job with distributed tracing headers.
    /// </summary>
    /// <param name="id">The unique enrichment job identifier.</param>
    /// <param name="apiUser">Optional tenant identifier.</param>
    /// <param name="correlationId">Optional request correlation identifier (X-Correlation-ID) as a <see cref="Guid"/>.</param>
    /// <param name="traceparent">Optional W3C traceparent header (traceparent).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current job lifecycle status (READY, PENDING, FAILED).</returns>
    Task<EnrichmentCollectionStatusResponse> GetEnrichmentStatusAsync(string id, string? apiUser, Guid? correlationId, string? traceparent = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads, verifies, and decompresses the .tar.gz results archive of a completed bulk enrichment job into memory-safe model instances.
    /// </summary>
    /// <remarks>
    /// <b>Memory Warning:</b> This method buffers all deserialized enrichment records into an in-memory list on the heap (<c>List&lt;EnrichmentResponse&gt;</c>).
    /// For high-volume pipelines, large batches, or memory-constrained environments, use <see cref="StreamEnrichmentCollectionAsync"/>
    /// to stream and process records with an $O(1)$ memory footprint via <see cref="IAsyncEnumerable{T}"/>.
    /// </remarks>
    /// <param name="downloadUrl">Archive download URL obtained from <see cref="EnrichTransactionCollectionResponse"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Read-only list of all enriched transaction records.</returns>
    Task<IReadOnlyList<EnrichmentResponse>> DownloadEnrichmentCollectionAsync(string downloadUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams and yields enrichment records on-the-fly from the bulk results archive in $O(1)$ memory without buffering the full dataset.
    /// </summary>
    /// <param name="downloadUrl">Archive download URL obtained from <see cref="EnrichTransactionCollectionResponse"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async enumerable yielding records as they are decompressed.</returns>
    IAsyncEnumerable<EnrichmentResponse> StreamEnrichmentCollectionAsync(string downloadUrl, CancellationToken cancellationToken = default);
}
