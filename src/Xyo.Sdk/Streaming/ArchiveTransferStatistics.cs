namespace Xyo.Sdk.Streaming;

/// <summary>
/// Mutable, single-writer statistics recorded while <see cref="TarStreamReader.StreamArchiveAsync"/> processes
/// an archive, so a caller can attach byte and entry counts to telemetry even when the stream fails partway
/// through (e.g. a stalled download): the values reflect whatever progress was made before the failure.
/// </summary>
/// <remarks>
/// Public only because it appears in the signature of the public <see cref="TarStreamReader.StreamArchiveAsync"/>
/// method; the only production caller is <c>XyoClient.StreamEnrichmentCollectionAsync</c>, which uses it to
/// attach <c>xyo.sdk.archive.entry_count</c> and <c>xyo.sdk.archive.bytes_inflated</c> to its client span.
/// </remarks>
public sealed class ArchiveTransferStatistics
{
    /// <summary>
    /// Total decompressed (inflated) bytes read from the archive so far.
    /// </summary>
    public long InflatedBytes { get; internal set; }

    /// <summary>
    /// Total tar entries encountered so far, including skipped directories and non-JSON entries.
    /// </summary>
    public int EntryCount { get; internal set; }
}
