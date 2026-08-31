using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xyo.Sdk.Exceptions;
using Xyo.Sdk.Streaming;
using Xyo.Sdk.Telemetry;

namespace Xyo.Sdk.Tests;

public class TarStreamReaderTests
{
    /// <summary>
    /// Listens to <see cref="XyoTelemetry.DownloadBoundTrippedCount"/> and returns the recorded
    /// <c>xyo.sdk.bound</c> tag values, in order.
    /// </summary>
    private static (MeterListener Listener, List<string?> BoundTags) ListenToBoundTrippedTags()
    {
        var boundTags = new List<string?>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, source) =>
            {
                if (instrument.Meter.Name == XyoTelemetry.Name && instrument.Name == "xyo.sdk.download.bound_tripped.count")
                {
                    source.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            lock (boundTags)
            {
                boundTags.Add(tags.ToArray().FirstOrDefault(t => t.Key == XyoTelemetry.BoundTagKey).Value?.ToString());
            }
        });
        listener.Start();
        return (listener, boundTags);
    }

    private static string CompleteRecordJson(string merchant) =>
        $@"{{ ""merchant"": ""{merchant}"", ""description"": ""Desc"", ""categories"": [""General""], ""logo"": ""https://cdn.xyo.financial/logo.png"", ""location"": ""London, UK"", ""address"": ""1 High St"" }}";

    private static byte[] CreateValidTarGz(params (string Name, string Content)[] entries)
    {
        using var tarMs = new MemoryStream();
        using (var tarWriter = new TarWriter(tarMs, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                byte[] contentBytes = Encoding.UTF8.GetBytes(content);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(contentBytes)
                };
                tarWriter.WriteEntry(entry);
            }
        }

        tarMs.Position = 0;
        using var gzMs = new MemoryStream();
        using (var gz = new GZipStream(gzMs, CompressionLevel.Optimal, leaveOpen: true))
        {
            tarMs.CopyTo(gz);
        }

        return gzMs.ToArray();
    }

    [Fact]
    public async Task ReadArchiveAsync_ValidTarGz_ExtractsAndDeserializesRecords()
    {
        string record1 = @"{ ""merchant"": ""Costa Coffee"", ""description"": ""Desc 1"", ""categories"": [""Food""], ""logo"": ""https://cdn.xyo.financial/logo1.png"", ""location"": ""London, UK"", ""address"": ""1 High St"" }";
        string record2 = @"{ ""merchant"": ""Starbucks"", ""description"": ""Desc 2"", ""categories"": [""Coffee""], ""logo"": ""https://cdn.xyo.financial/logo2.png"", ""location"": ""Seattle, US"", ""address"": ""1 Pike St"" }";

        byte[] archiveBytes = CreateValidTarGz(
            ("001.json", record1),
            ("002.json", record2),
            ("ignore.txt", "some non-json text")
        );

        using var ms = new MemoryStream(archiveBytes);
        var results = await TarStreamReader.ReadArchiveAsync(ms);

        Assert.Equal(2, results.Count);
        Assert.Equal("Costa Coffee", results[0].Merchant);
        Assert.Equal("Starbucks", results[1].Merchant);
    }

    [Fact]
    public async Task ReadArchiveAsync_PathTraversalEntry_ThrowsXyoClientException()
    {
        string record = @"{ ""merchant"": ""Evil"" }";
        byte[] archiveBytes = CreateValidTarGz(("../evil.json", record));

        using var ms = new MemoryStream(archiveBytes);
        var ex = await Assert.ThrowsAsync<XyoClientException>(() => TarStreamReader.ReadArchiveAsync(ms));
        Assert.Contains("Path traversal detected", ex.Message);
    }

    [Fact]
    public async Task ReadArchiveAsync_ControlCharactersInName_ThrowsXyoClientException()
    {
        string record = @"{ ""merchant"": ""Evil"" }";
        byte[] archiveBytes = CreateValidTarGz(("evil\r\n.json", record));

        using var ms = new MemoryStream(archiveBytes);
        var ex = await Assert.ThrowsAsync<XyoClientException>(() => TarStreamReader.ReadArchiveAsync(ms));
        Assert.Contains("forbidden control characters", ex.Message);
    }

    [Fact]
    public async Task ReadArchiveAsync_EntryExceedingMaxEntryBytes_ThrowsXyoClientException()
    {
        string largeRecord = @"{ ""merchant"": """ + new string('A', 2000) + @""" }";
        byte[] archiveBytes = CreateValidTarGz(("large.json", largeRecord));

        using var ms = new MemoryStream(archiveBytes);
        var ex = await Assert.ThrowsAsync<XyoClientException>(() => TarStreamReader.ReadArchiveAsync(ms, maxEntryBytes: 100));
        Assert.Contains("exceeds maximum size limit", ex.Message);
    }

    [Fact]
    public async Task ReadArchiveAsync_AggregateDecompressedSizeExceedingMaxDecompressedBytes_ThrowsXyoClientException()
    {
        // Each entry individually stays well under maxEntryBytes; it is only the SUM across entries that
        // exceeds maxDecompressedBytes. This specifically exercises the aggregate bound, not the per-entry
        // one -- the bound that was missing entirely before the archive-download-bytes bug was fixed.
        // Schema-complete (all required fields present, so an individual entry under the threshold
        // deserializes successfully rather than failing fast on a missing-field error) with the bulk of
        // its size in one huge quoted string value, so the JSON reader must keep pulling bytes from the
        // stream while scanning for the closing quote -- giving the aggregate bound a chance to trip
        // mid-read once the cumulative total across entries crosses it.
        string bigRecord =
            @"{ ""merchant"": ""M"", ""description"": """ + new string('A', 80_000) + @""", " +
            @"""categories"": [""General""], ""logo"": ""https://cdn.xyo.financial/logo.png"", " +
            @"""location"": ""London, UK"", ""address"": ""1 High St"" }";
        byte[] archiveBytes = CreateValidTarGz(
            ("001.json", bigRecord),
            ("002.json", bigRecord),
            ("003.json", bigRecord));

        using var ms = new MemoryStream(archiveBytes);
        var ex = await Assert.ThrowsAsync<XyoClientException>(() =>
            TarStreamReader.ReadArchiveAsync(ms, maxDecompressedBytes: 150_000, maxEntryBytes: 200_000));

        Assert.Contains("decompressed content size exceeded maximum allowed byte size", ex.Message);
    }

    [Fact]
    public async Task ReadArchiveAsync_WireSizeExceedingMaxArchiveBytes_ThrowsXyoClientException()
    {
        string record = @"{ ""merchant"": ""M"", ""description"": ""Desc"", ""categories"": [""General""], ""logo"": ""https://cdn.xyo.financial/logo.png"", ""location"": ""London, UK"", ""address"": ""1 High St"" }";
        byte[] archiveBytes = CreateValidTarGz(("001.json", record));

        using var ms = new MemoryStream(archiveBytes);
        var ex = await Assert.ThrowsAsync<XyoClientException>(() =>
            TarStreamReader.ReadArchiveAsync(ms, maxArchiveBytes: 50));

        Assert.Contains("download wire size exceeded maximum allowed byte size", ex.Message);
    }

    [Fact]
    public async Task ReadArchiveAsync_EntryCountExceedingMaxTarEntries_ThrowsXyoClientException()
    {
        string CompleteRecord(string merchant) =>
            $@"{{ ""merchant"": ""{merchant}"", ""description"": ""Desc"", ""categories"": [""General""], ""logo"": ""https://cdn.xyo.financial/logo.png"", ""location"": ""London, UK"", ""address"": ""1 High St"" }}";

        byte[] archiveBytes = CreateValidTarGz(
            ("1.json", CompleteRecord("M1")),
            ("2.json", CompleteRecord("M2")),
            ("3.json", CompleteRecord("M3"))
        );

        using var ms = new MemoryStream(archiveBytes);
        var ex = await Assert.ThrowsAsync<XyoClientException>(() => TarStreamReader.ReadArchiveAsync(ms, maxTarEntries: 2));
        Assert.Contains("exceeds maximum entry count limit", ex.Message);
    }

    [Fact]
    public async Task StreamArchiveAsync_StreamsRecordsSuccessfully()
    {
        string record1 = @"{ ""merchant"": ""Merchant A"", ""description"": ""Desc A"", ""categories"": [""General""], ""logo"": ""https://cdn.xyo.financial/a.png"", ""location"": ""London, UK"", ""address"": ""1 High St"" }";
        string record2 = @"{ ""merchant"": ""Merchant B"", ""description"": ""Desc B"", ""categories"": [""General""], ""logo"": ""https://cdn.xyo.financial/b.png"", ""location"": ""Seattle, US"", ""address"": ""1 Pike St"" }";

        byte[] archiveBytes = CreateValidTarGz(
            ("a.json", record1),
            ("empty.json", ""),
            ("b.json", record2)
        );

        using var ms = new MemoryStream(archiveBytes);
        var streamed = new System.Collections.Generic.List<Xyo.Generated.Model.EnrichmentResponse>();
        await foreach (var item in TarStreamReader.StreamArchiveAsync(ms))
        {
            streamed.Add(item);
        }

        Assert.Equal(2, streamed.Count);
        Assert.Equal("Merchant A", streamed[0].Merchant);
        Assert.Equal("Merchant B", streamed[1].Merchant);
    }

    [Fact]
    public async Task ReadArchiveAsync_CorruptedJson_ThrowsXyoClientException()
    {
        byte[] archiveBytes = CreateValidTarGz(("bad.json", "{ not valid json :::"));

        using var ms = new MemoryStream(archiveBytes);
        var ex = await Assert.ThrowsAsync<XyoClientException>(() => TarStreamReader.ReadArchiveAsync(ms));
        Assert.Contains("Failed to deserialize JSON record", ex.Message);
    }

    [Fact]
    public void BoundedReadStream_ReadSpan_ReadsExpectedDataSuccessfully()
    {
        byte[] sourceData = Encoding.UTF8.GetBytes("Hello, BoundedReadStream Span Read!");
        using var memoryStream = new MemoryStream(sourceData);
        using var boundedStream = new TarStreamReader.BoundedReadStream(memoryStream, maxBytes: 1000);

        Span<byte> buffer = stackalloc byte[10];
        int bytesRead = boundedStream.Read(buffer);

        Assert.Equal(10, bytesRead);
        Assert.Equal("Hello, Bou", Encoding.UTF8.GetString(buffer));
    }

    [Fact]
    public void BoundedReadStream_ReadSpan_ExceedingLimit_ThrowsXyoClientException()
    {
        byte[] sourceData = new byte[200];
        using var memoryStream = new MemoryStream(sourceData);
        using var boundedStream = new TarStreamReader.BoundedReadStream(memoryStream, maxBytes: 50, entryName: "test.json");

        var ex = Assert.Throws<XyoClientException>(() =>
        {
            Span<byte> buffer = new byte[100];
            boundedStream.Read(buffer);
        });
        Assert.Contains("exceeds maximum size limit", ex.Message);
        Assert.Contains("test.json", ex.Message);
    }

    [Fact]
    public void BoundedReadStream_ReadSpan_ReturnsZeroAtEndOfStream()
    {
        byte[] sourceData = [1, 2, 3];
        using var memoryStream = new MemoryStream(sourceData);
        using var boundedStream = new TarStreamReader.BoundedReadStream(memoryStream, maxBytes: 100);

        Span<byte> buffer = stackalloc byte[10];
        int bytesRead1 = boundedStream.Read(buffer);
        Assert.Equal(3, bytesRead1);

        int bytesRead2 = boundedStream.Read(buffer);
        Assert.Equal(0, bytesRead2);
    }

    [Fact]
    public async Task StreamArchiveAsync_NullStream_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in TarStreamReader.StreamArchiveAsync(null!))
            {
            }
        });
    }

    [Fact]
    public async Task ReadArchiveAsync_CorruptGzipContainer_ThrowsXyoClientException()
    {
        // Not a gzip stream at all. The failure surfaces from the tar reader pulling through GZipStream, so
        // it must be translated rather than escaping as a raw InvalidDataException.
        byte[] garbage = Encoding.UTF8.GetBytes("this is definitely not a gzip archive, not even close");

        using var ms = new MemoryStream(garbage);
        var ex = await Assert.ThrowsAsync<XyoClientException>(() => TarStreamReader.ReadArchiveAsync(ms));

        Assert.Contains("Corrupted or invalid tar archive entry", ex.Message);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ex.StatusCode);
    }

    [Fact]
    public async Task ReadArchiveAsync_TruncatedArchive_ThrowsXyoClientException()
    {
        // A valid archive cut in half mid-entry: the gzip trailer is missing, which is what a connection
        // dropped part-way through a download actually looks like on disk.
        byte[] full = CreateValidTarGz(("001.json", CompleteRecordJson("M1")));
        byte[] truncated = full.AsSpan(0, full.Length / 2).ToArray();

        using var ms = new MemoryStream(truncated);
        var ex = await Assert.ThrowsAsync<XyoClientException>(() => TarStreamReader.ReadArchiveAsync(ms));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, ex.StatusCode);
    }

    [Fact]
    public async Task ReadArchiveAsync_DirectoryAndNonJsonEntries_AreSkipped()
    {
        // Directories and non-regular entries must be skipped without aborting the archive, and without
        // being mistaken for records.
        using var tarMs = new MemoryStream();
        using (var tarWriter = new TarWriter(tarMs, TarEntryFormat.Pax, leaveOpen: true))
        {
            tarWriter.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "batch/"));
            tarWriter.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "batch/notes.txt")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("ignored"))
            });
            tarWriter.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "batch/001.json")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(CompleteRecordJson("Only Record")))
            });
        }
        tarMs.Position = 0;
        using var gzMs = new MemoryStream();
        using (var gz = new GZipStream(gzMs, CompressionLevel.Optimal, leaveOpen: true))
        {
            tarMs.CopyTo(gz);
        }

        using var ms = new MemoryStream(gzMs.ToArray());
        var results = await TarStreamReader.ReadArchiveAsync(ms);

        Assert.Single(results);
        Assert.Equal("Only Record", results[0].Merchant);
    }

    [Fact]
    public async Task ReadArchiveAsync_EntryDeserializingToNull_IsSkipped()
    {
        // A JSON literal null deserializes to a null record, which must be skipped rather than yielded as a
        // null element into the caller's sequence.
        byte[] archiveBytes = CreateValidTarGz(
            ("001.json", "null"),
            ("002.json", CompleteRecordJson("Real Record")));

        using var ms = new MemoryStream(archiveBytes);
        var results = await TarStreamReader.ReadArchiveAsync(ms);

        Assert.Single(results);
        Assert.Equal("Real Record", results[0].Merchant);
    }

    [Fact]
    public async Task StreamArchiveAsync_AlreadyCancelledToken_PropagatesOperationCanceled()
    {
        // Cancellation must escape as OperationCanceledException rather than being wrapped as a corrupt
        // archive, so a caller cancelling a download can still distinguish it from a bad payload.
        byte[] archiveBytes = CreateValidTarGz(("001.json", CompleteRecordJson("M1")));
        using var ms = new MemoryStream(archiveBytes);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in TarStreamReader.StreamArchiveAsync(ms, cancellationToken: cts.Token))
            {
            }
        });
    }

    [Fact]
    public void BoundedReadStream_NullInnerStream_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new TarStreamReader.BoundedReadStream(null!, maxBytes: 100));
    }

    [Fact]
    public void BoundedReadStream_SynchronousArrayRead_EnforcesTheBound()
    {
        // The synchronous byte[] overload enforces the same ceiling as the async path. Nothing in the SDK
        // reaches it today, but it is part of the type's contract and a caller of the public
        // TarStreamReader API can supply a stream that drives it.
        byte[] sourceData = Encoding.UTF8.GetBytes(new string('A', 200));
        using var memoryStream = new MemoryStream(sourceData);
        using var boundedStream = new TarStreamReader.BoundedReadStream(memoryStream, maxBytes: 100, entryName: "sync.json");

        var buffer = new byte[64];
        int first = boundedStream.Read(buffer, 0, buffer.Length);
        Assert.Equal(64, first); // 64 of 100 consumed, still inside the bound

        var ex = Assert.Throws<XyoClientException>(() => boundedStream.Read(buffer, 0, buffer.Length));
        Assert.Contains("sync.json", ex.Message);
    }

    [Fact]
    public void BoundedReadStream_UnlabelledBound_ReportsTheGenericArchiveMessage()
    {
        // With neither an entry name nor a bound label, the message falls back to the generic form.
        byte[] sourceData = Encoding.UTF8.GetBytes(new string('A', 200));
        using var memoryStream = new MemoryStream(sourceData);
        using var boundedStream = new TarStreamReader.BoundedReadStream(memoryStream, maxBytes: 10);

        var buffer = new byte[64];
        var ex = Assert.Throws<XyoClientException>(() => boundedStream.Read(buffer, 0, buffer.Length));

        Assert.Contains("Archive download exceeded maximum allowed byte size", ex.Message);
        Assert.DoesNotContain("wire size", ex.Message);
        Assert.DoesNotContain("decompressed content size", ex.Message);
    }

    [Fact]
    public void BoundedReadStream_UnsupportedOperations_ThrowNotSupported()
    {
        // CanSeek is false, so Length and Position are unsupported by contract rather than delegated to an
        // inner stream that may answer them inconsistently. The stream is read-only in both directions.
        using var memoryStream = new MemoryStream(new byte[8]);
        using var boundedStream = new TarStreamReader.BoundedReadStream(memoryStream, maxBytes: 100);

        Assert.True(boundedStream.CanRead);
        Assert.False(boundedStream.CanSeek);
        Assert.False(boundedStream.CanWrite);

        Assert.Throws<NotSupportedException>(() => boundedStream.Length);
        Assert.Throws<NotSupportedException>(() => boundedStream.Position);
        Assert.Throws<NotSupportedException>(() => boundedStream.Position = 0);
        Assert.Throws<NotSupportedException>(() => boundedStream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => boundedStream.SetLength(1));
        Assert.Throws<NotSupportedException>(() => boundedStream.Write(new byte[1], 0, 1));

        boundedStream.Flush(); // delegates to the inner stream and must not throw
    }

    // ---------- xyo.sdk.download.bound_tripped.count tag coverage (PR review S1/S10) ----------
    //
    // The tag value is a structural constant passed at each call site (XyoTelemetry.Bound*), not derived by
    // matching the human-readable exception message text, precisely so these tests keep meaning even if a
    // message is reworded for clarity. Previously this counter had no test coverage at all.

    [Fact]
    public async Task StreamArchiveAsync_WireSizeExceeded_RecordsMaxArchiveBytesBoundTag()
    {
        var (listener, boundTags) = ListenToBoundTrippedTags();
        using var _l = listener;

        byte[] archiveBytes = CreateValidTarGz(("001.json", CompleteRecordJson("M1")));
        using var ms = new MemoryStream(archiveBytes);

        await Assert.ThrowsAsync<XyoClientException>(() => TarStreamReader.ReadArchiveAsync(ms, maxArchiveBytes: 50));

        Assert.Contains(XyoTelemetry.BoundMaxArchiveBytes, boundTags);
    }

    [Fact]
    public async Task StreamArchiveAsync_DecompressedSizeExceeded_RecordsMaxDecompressedBytesBoundTag()
    {
        var (listener, boundTags) = ListenToBoundTrippedTags();
        using var _l = listener;

        string bigRecord =
            @"{ ""merchant"": ""M"", ""description"": """ + new string('A', 80_000) + @""", " +
            @"""categories"": [""General""], ""logo"": ""https://cdn.xyo.financial/logo.png"", " +
            @"""location"": ""London, UK"", ""address"": ""1 High St"" }";
        byte[] archiveBytes = CreateValidTarGz(
            ("001.json", bigRecord),
            ("002.json", bigRecord),
            ("003.json", bigRecord));
        using var ms = new MemoryStream(archiveBytes);

        await Assert.ThrowsAsync<XyoClientException>(() =>
            TarStreamReader.ReadArchiveAsync(ms, maxDecompressedBytes: 150_000, maxEntryBytes: 200_000));

        Assert.Contains(XyoTelemetry.BoundMaxDecompressedBytes, boundTags);
    }

    [Fact]
    public async Task StreamArchiveAsync_TarHeaderDeclaresOversizeEntry_RecordsMaxEntryBytesBoundTag()
    {
        // The tar header's own declared entry length trips this, not BoundedReadStream counting bytes as
        // they stream past -- see the next test for that path. Both must record the same tag (S2): whether
        // this bound shows up in metrics must not depend on whether the header was honest about the size of
        // an attacker-controlled entry.
        var (listener, boundTags) = ListenToBoundTrippedTags();
        using var _l = listener;

        string largeRecord = @"{ ""merchant"": """ + new string('A', 2000) + @""" }";
        byte[] archiveBytes = CreateValidTarGz(("large.json", largeRecord));
        using var ms = new MemoryStream(archiveBytes);

        await Assert.ThrowsAsync<XyoClientException>(() => TarStreamReader.ReadArchiveAsync(ms, maxEntryBytes: 100));

        Assert.Contains(XyoTelemetry.BoundMaxEntryBytes, boundTags);
    }

    [Fact]
    public void BoundedReadStream_EntryStreamExceedsLimit_RecordsMaxEntryBytesBoundTag()
    {
        var (listener, boundTags) = ListenToBoundTrippedTags();
        using var _l = listener;

        byte[] sourceData = new byte[200];
        using var memoryStream = new MemoryStream(sourceData);
        using var boundedStream = new TarStreamReader.BoundedReadStream(memoryStream, maxBytes: 50,
            entryName: "test.json", boundTag: XyoTelemetry.BoundMaxEntryBytes);

        Assert.Throws<XyoClientException>(() =>
        {
            Span<byte> buffer = new byte[100];
            boundedStream.Read(buffer);
        });

        Assert.Contains(XyoTelemetry.BoundMaxEntryBytes, boundTags);
    }

    [Fact]
    public async Task StreamArchiveAsync_EntryCountExceeded_RecordsMaxTarEntriesBoundTag()
    {
        byte[] archiveBytes = CreateValidTarGz(
            ("1.json", CompleteRecordJson("M1")),
            ("2.json", CompleteRecordJson("M2")),
            ("3.json", CompleteRecordJson("M3")));
        using var ms = new MemoryStream(archiveBytes);

        var (listener, boundTags) = ListenToBoundTrippedTags();
        using var _l = listener;

        await Assert.ThrowsAsync<XyoClientException>(() => TarStreamReader.ReadArchiveAsync(ms, maxTarEntries: 2));

        Assert.Contains(XyoTelemetry.BoundMaxTarEntries, boundTags);
    }
}
