namespace Xyo.Sdk.Streaming;

/// <summary>
/// Mutable, single-writer statistics recorded while <see cref="TarStreamReader"/>'s internal
/// statistics-reporting overload of <c>StreamArchiveAsync</c> processes an archive, so a caller can attach
/// byte and entry counts to telemetry even when the stream fails partway through (e.g. a stalled download):
/// the values reflect whatever progress was made before the failure.
/// </summary>
/// <remarks>
/// Internal: it appears only in the signature of the internal statistics-reporting overload of
/// <c>TarStreamReader.StreamArchiveAsync</c>, not the public one, so it never became a permanent public type
/// on this package's surface. The only production caller is
/// <c>XyoClient.StreamEnrichmentCollectionAsync</c>, which uses it to attach
/// <c>xyo.sdk.archive.entry_count</c> and <c>xyo.sdk.archive.bytes_inflated</c> to its client span.
/// <c>InternalsVisibleTo Xyo.Sdk.Tests</c> in the csproj keeps it testable.
/// </remarks>
internal sealed class ArchiveTransferStatistics
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
