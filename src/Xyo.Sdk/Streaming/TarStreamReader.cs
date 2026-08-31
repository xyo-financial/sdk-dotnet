using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xyo.Generated.Model;
using Xyo.Sdk.Client;
using Xyo.Sdk.Exceptions;
using Xyo.Sdk.Telemetry;

namespace Xyo.Sdk.Streaming;

/// <summary>
/// Provides memory-safe, bounded streaming decompression for .tar.gz bulk enrichment collection archives.
/// Enforces CWE-400 (Decompression Bomb) and CWE-22 (Zip Slip) mitigations.
/// </summary>
public static class TarStreamReader
{
    /// <summary>
    /// Reads and deserializes all enrichment records from a compressed .tar.gz stream into a list.
    /// </summary>
    /// <remarks>
    /// <b>Memory Warning:</b> Buffers all deserialized enrichment records into an in-memory list on the heap.
    /// For high-volume pipelines or large datasets, prefer
    /// <see cref="StreamArchiveAsync(Stream, long, long, long, int, CancellationToken)"/> for streaming
    /// processing with an $O(1)$ memory footprint.
    /// </remarks>
    public static async Task<IReadOnlyList<EnrichmentResponse>> ReadArchiveAsync(
        Stream compressedStream,
        long maxArchiveBytes = 104_857_600, // 100 MiB
        long maxDecompressedBytes = 2_097_152_000, // 2000 MiB
        long maxEntryBytes = 10_485_760,   // 10 MiB
        int maxTarEntries = 50_000,
        CancellationToken cancellationToken = default)
    {
        var results = new List<EnrichmentResponse>();
        await foreach (var item in StreamArchiveAsync(compressedStream, maxArchiveBytes, maxDecompressedBytes, maxEntryBytes, maxTarEntries, statistics: null, cancellationToken).ConfigureAwait(false))
        {
            results.Add(item);
        }
        return results;
    }

    /// <summary>
    /// Streams and yields enrichment records on-the-fly from a compressed .tar.gz stream with $O(1)$ memory footprint.
    /// </summary>
    public static IAsyncEnumerable<EnrichmentResponse> StreamArchiveAsync(
        Stream compressedStream,
        long maxArchiveBytes = 104_857_600,
        long maxDecompressedBytes = 2_097_152_000,
        long maxEntryBytes = 10_485_760,
        int maxTarEntries = 50_000,
        CancellationToken cancellationToken = default) =>
        StreamArchiveAsync(compressedStream, maxArchiveBytes, maxDecompressedBytes, maxEntryBytes, maxTarEntries,
            statistics: null, cancellationToken);

    /// <summary>
    /// Streams and yields enrichment records on-the-fly from a compressed .tar.gz stream with $O(1)$ memory
    /// footprint, additionally reporting live progress through <paramref name="statistics"/>.
    /// </summary>
    /// <remarks>
    /// Internal rather than a public overload: adding a parameter to the published, six-parameter public
    /// <see cref="StreamArchiveAsync(Stream, long, long, long, int, CancellationToken)"/> would bake a
    /// binary-breaking change into every assembly compiled against an earlier build (optional parameters
    /// are resolved at the caller's call site, not the callee), with no corresponding major version bump.
    /// <see cref="ArchiveTransferStatistics"/> is internal for the same reason -- see its own remarks.
    /// </remarks>
    internal static async IAsyncEnumerable<EnrichmentResponse> StreamArchiveAsync(
        Stream compressedStream,
        long maxArchiveBytes,
        long maxDecompressedBytes,
        long maxEntryBytes,
        int maxTarEntries,
        ArchiveTransferStatistics? statistics,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (compressedStream == null)
        {
            throw new ArgumentNullException(nameof(compressedStream));
        }

        // Bounded stream wrapper to track wire (compressed) bytes read against maxArchiveBytes
        var wireStream = new BoundedReadStream(compressedStream, maxArchiveBytes,
            boundLabel: "download wire size", boundTag: XyoTelemetry.BoundMaxArchiveBytes);

        using var gzipStream = new GZipStream(wireStream, CompressionMode.Decompress, leaveOpen: true);

        // Independent bound on total INFLATED bytes -- this is the actual decompression-bomb defense.
        // maxArchiveBytes above only limits bytes taken off the wire before inflation.
        var inflatedStream = new BoundedReadStream(gzipStream, maxDecompressedBytes,
            boundLabel: "decompressed content size", boundTag: XyoTelemetry.BoundMaxDecompressedBytes);

        using var tarReader = new TarReader(inflatedStream, leaveOpen: true);

        int entryCount = 0;

        while (true)
        {
            TarEntry? entry;
            try
            {
                entry = await tarReader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (XyoException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new XyoClientException(System.Net.HttpStatusCode.UnprocessableEntity, $"Corrupted or invalid tar archive entry: {ex.Message}", innerException: ex);
            }

            if (entry == null)
            {
                break;
            }

            entryCount++;
            if (statistics != null)
            {
                statistics.EntryCount = entryCount;
                statistics.InflatedBytes = inflatedStream.TotalBytesRead;
            }
            if (entryCount > maxTarEntries)
            {
                if (XyoTelemetry.DownloadBoundTrippedCount.Enabled)
                {
                    XyoTelemetry.DownloadBoundTrippedCount.Add(1,
                        new KeyValuePair<string, object?>(XyoTelemetry.BoundTagKey, XyoTelemetry.BoundMaxTarEntries));
                }
                throw new XyoClientException(System.Net.HttpStatusCode.UnprocessableEntity,
                    $"Tar archive exceeds maximum entry count limit ({maxTarEntries} entries). Possible tar bomb DoS attack.");
            }

            // Path Traversal / Zip Slip Defense
            string entryName = entry.Name;
            ValidateEntryName(entryName);

            // Skip directories or non-regular files
            if (entry.EntryType != TarEntryType.RegularFile && entry.EntryType != TarEntryType.V7RegularFile)
            {
                continue;
            }

            // Only process .json files
            if (!entryName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.DataStream == null || entry.Length == 0)
            {
                continue;
            }

            if (entry.Length > maxEntryBytes)
            {
                // Counted here too, not just when BoundedReadStream trips below: the tar header declares its
                // own entry length, which an attacker controls, so whether this bound shows up in metrics
                // must not depend on whether the header happened to be honest about the oversize entry.
                if (XyoTelemetry.DownloadBoundTrippedCount.Enabled)
                {
                    XyoTelemetry.DownloadBoundTrippedCount.Add(1,
                        new KeyValuePair<string, object?>(XyoTelemetry.BoundTagKey, XyoTelemetry.BoundMaxEntryBytes));
                }
                throw new XyoClientException(System.Net.HttpStatusCode.UnprocessableEntity,
                    $"Tar entry '{entryName}' exceeds maximum size limit ({maxEntryBytes} bytes). Decompression bomb rejected.");
            }

            EnrichmentResponse? response;
            try
            {
                var entryBoundedStream = new BoundedReadStream(entry.DataStream, maxEntryBytes, entryName,
                    boundTag: XyoTelemetry.BoundMaxEntryBytes);
                response = await JsonSerializer.DeserializeAsync<EnrichmentResponse>(entryBoundedStream, XyoClient.SerializerOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (XyoException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new XyoClientException(System.Net.HttpStatusCode.UnprocessableEntity,
                    $"Failed to deserialize JSON record from archive entry '{entryName}': {ex.Message}", innerException: ex);
            }

            if (response != null)
            {
                if (statistics != null)
                {
                    statistics.InflatedBytes = inflatedStream.TotalBytesRead;
                }
                yield return response;
            }
        }
    }

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        // Control characters check (CWE-117 log injection defense)
        foreach (char c in name)
        {
            if (char.IsControl(c))
            {
                throw new XyoClientException(System.Net.HttpStatusCode.BadRequest, "Tar entry contains forbidden control characters.");
            }
        }

        // Path traversal checks (CWE-22 / CWE-29)
        if (name.Contains("..", StringComparison.Ordinal) ||
            name.StartsWith("/", StringComparison.Ordinal) ||
            name.StartsWith("\\", StringComparison.Ordinal) ||
            Path.IsPathRooted(name))
        {
            throw new XyoClientException(System.Net.HttpStatusCode.BadRequest, $"Path traversal detected in archive entry name: '{name}'.");
        }
    }

    internal sealed class BoundedReadStream : Stream
    {
        private readonly Stream _innerStream;
        private readonly long _maxBytes;
        private readonly string? _entryName;
        private readonly string? _boundLabel;
        private readonly string _boundTag;
        private long _totalBytesRead;

        /// <param name="innerStream">The stream to read through.</param>
        /// <param name="maxBytes">The byte ceiling that trips <see cref="ThrowMaxBytesExceeded"/>.</param>
        /// <param name="entryName">Set for a per-entry bound; included in the thrown exception's message.</param>
        /// <param name="boundLabel">Human-readable label interpolated into the thrown exception's message only.</param>
        /// <param name="boundTag">
        /// The <c>xyo.sdk.bound</c> metric tag value recorded when this bound trips. Deliberately a separate
        /// parameter from <paramref name="boundLabel"/> rather than derived from it: coupling the metric
        /// classification to prose meant for a human reader let a message reword silently reclassify which
        /// bound tripped (see the PR review, C1/S1). Always pass one of <see cref="XyoTelemetry"/>'s
        /// <c>Bound*</c> constants.
        /// </param>
        public BoundedReadStream(Stream innerStream, long maxBytes, string? entryName = null, string? boundLabel = null,
            string boundTag = XyoTelemetry.BoundMaxArchiveBytes)
        {
            _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
            _maxBytes = maxBytes;
            _entryName = entryName;
            _boundLabel = boundLabel;
            _boundTag = boundTag;
        }

        /// <summary>
        /// Total bytes read through this wrapper so far, exposed so a caller can attach a byte count to
        /// telemetry even when the stream is still short of <see cref="_maxBytes"/> (e.g. progress recorded
        /// before an unrelated failure elsewhere in the pipeline).
        /// </summary>
        internal long TotalBytesRead => _totalBytesRead;

        public override bool CanRead => _innerStream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        // CanSeek is false, so Length/Position are unsupported by contract -- thrown explicitly rather than
        // delegated to the inner stream, which for a network stream would throw its own NotSupportedException
        // anyway, just less predictably (some inner stream types instead return a stale or default value).
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                _totalBytesRead += read;
                if (_totalBytesRead > _maxBytes)
                {
                    ThrowMaxBytesExceeded();
                }
            }
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _innerStream.Read(buffer, offset, count);
            if (read > 0)
            {
                _totalBytesRead += read;
                if (_totalBytesRead > _maxBytes)
                {
                    ThrowMaxBytesExceeded();
                }
            }
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = _innerStream.Read(buffer);
            if (read > 0)
            {
                _totalBytesRead += read;
                if (_totalBytesRead > _maxBytes)
                {
                    ThrowMaxBytesExceeded();
                }
            }
            return read;
        }

        private void ThrowMaxBytesExceeded()
        {
            if (XyoTelemetry.DownloadBoundTrippedCount.Enabled)
            {
                XyoTelemetry.DownloadBoundTrippedCount.Add(1,
                    new KeyValuePair<string, object?>(XyoTelemetry.BoundTagKey, _boundTag));
            }

            if (!string.IsNullOrWhiteSpace(_entryName))
            {
                throw new XyoClientException(System.Net.HttpStatusCode.UnprocessableEntity,
                    $"Tar entry '{_entryName}' exceeds maximum size limit ({_maxBytes} bytes). Decompression bomb rejected.");
            }

            if (!string.IsNullOrWhiteSpace(_boundLabel))
            {
                throw new XyoClientException(System.Net.HttpStatusCode.UnprocessableEntity,
                    $"Archive {_boundLabel} exceeded maximum allowed byte size ({_maxBytes} bytes). Decompression bomb ingestion rejected.");
            }

            throw new XyoClientException(System.Net.HttpStatusCode.UnprocessableEntity,
                $"Archive download exceeded maximum allowed byte size ({_maxBytes} bytes). Decompression bomb ingestion rejected.");
        }

        public override void Flush() => _innerStream.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
