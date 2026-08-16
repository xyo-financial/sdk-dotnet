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
    public async Task ReadArchiveAsync_EntryCountExceedingMaxTarEntries_ThrowsXyoClientException()
    {
        byte[] archiveBytes = CreateValidTarGz(
            ("1.json", @"{""merchant"":""M1""}"),
            ("2.json", @"{""merchant"":""M2""}"),
            ("3.json", @"{""merchant"":""M3""}")
        );

        using var ms = new MemoryStream(archiveBytes);
        var ex = await Assert.ThrowsAsync<XyoClientException>(() => TarStreamReader.ReadArchiveAsync(ms, maxTarEntries: 2));
        Assert.Contains("exceeds maximum entry count limit", ex.Message);
    }
}
