using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xyo.Sdk.Exceptions;
using Xyo.Sdk.Streaming;

namespace Xyo.Sdk.Tests;

public class TarStreamReaderTests
{
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
}
