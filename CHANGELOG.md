# 📜 Changelog

All notable changes to the XYO Financial .NET Client SDK (`sdk-dotnet`) will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Added
- Automated spec regeneration pipeline (`.github/workflows/generate.yml`) listening to `repository_dispatch` from `xyo-financial/specs`.
- GitHub release workflow (`.github/workflows/release.yml`) with SBOM generation, SHA-256 checksums, and artifact provenance attestations.
- Standalone `.NET LTS Support Schedule` SVG graphic in `docs/lts_schedule.svg` and proactive 3-month LTS sunset policy in `SECURITY.md`.
- `XyoClientConfig.DownloadConnectTimeout` and `XyoClientConfig.ReadIdleTimeout` (also on `XyoClientOptions`), splitting the two unrelated roles previously served by `DownloadTimeout`: a deadline on connection establishment, redirect resolution, and response header retrieval (`DownloadConnectTimeout`, default 10 minutes), and a per-read idle stall timeout during archive streaming (`ReadIdleTimeout`, default 120 seconds). Both (and the obsolete `DownloadTimeout` seed) are validated at construction: a non-positive value throws `ArgumentOutOfRangeException`, and `Timeout.InfiniteTimeSpan` is accepted as an explicit opt-out of the bound.
- `AddXyoClient(Action<XyoClientOptions>)` now honours live `XyoClientOptions` reloads (e.g. `appsettings.json` changes) on the already-resolved singleton `IXyoClient`, via `IOptionsMonitor<XyoClientOptions>`. The registered lifetime stays `Singleton`; a reload is only rebuilt on its change token, never per call, and a reload that fails validation is rejected, leaving the client on its last valid configuration and logging the failure rather than swallowing it (US-DOTNET-004). A reload is now rejected whenever it would leave the client without a credential, not only when it fails URL/header validation, and a change to a differently named `XyoClientOptions` instance elsewhere in the container no longer reaches the client at all.
- `XyoClientOptions.LoggerFactory` / `XyoClientConfig.LoggerFactory`: optional `ILoggerFactory` the SDK builds its single internal logger from, once, at construction time. The DI registration wires in the container's `ILoggerFactory` automatically when the caller has not set one explicitly.
- First-class OpenTelemetry instrumentation (`Xyo.Sdk` `ActivitySource` and `Meter`): a client span per public operation with span status derived from the typed exception hierarchy, and request/duration/rate-limit/redirect-refused/download-bound-tripped metrics. Inert (no allocation) with no listener registered. No span attribute, metric tag, or log message the SDK constructs ever contains the API key; a span's error status carries a low-cardinality outcome classification only, never a raw exception or server response message.

### Fixed
- The client span's own context now propagates onto the outbound `traceparent` header whenever a span exists, including when the parent comes from an ambient `Activity.Current` (e.g. an application that has already adopted OpenTelemetry) rather than a caller-supplied header string.
- The outbound `traceparent` header is now always valid W3C format.
- `StreamEnrichmentCollectionAsync` now records its request counter, duration, and span completion when the consumer abandons enumeration early (`break`, `.Take(n)`, `.FirstOrDefaultAsync()`), tagged with a distinct `abandoned` outcome.
- The `xyo.sdk.download.bound_tripped.count` `xyo.sdk.bound` tag value is now a fixed constant passed at each call site rather than derived by string-matching a human-readable message fragment. The `max_entry_bytes` bound is now also counted when a tar header declares an oversize entry, not only when the stream reader trips over it.
- The `Xyo.Sdk` `ActivitySource`/`Meter` version no longer includes the build's full git SHA, keeping metric streams stable across commits within the same package version.

### Changed
- **Behaviour change:** the effective stall timeout for archive downloads (`StreamEnrichmentCollectionAsync` / `DownloadEnrichmentCollectionAsync`) drops from the old `DownloadTimeout` default of 10 minutes to `ReadIdleTimeout`'s default of 120 seconds. A peer that previously had up to 10 minutes to produce the next byte of a download before being dropped is now dropped after 2 minutes. This only affects stall detection; the connection/redirect deadline (`DownloadConnectTimeout`) keeps the previous 10-minute default. Set `ReadIdleTimeout` explicitly to restore a longer allowance if your environment needs one.

### Deprecated
- `XyoClientConfig.DownloadTimeout` and `XyoClientOptions.DownloadTimeout` are marked `[Obsolete]` in favour of `DownloadConnectTimeout` and `ReadIdleTimeout`. When set, `DownloadTimeout` still seeds both replacement properties, so existing configuration keeps working, but it is scheduled for removal in the next major version.

---

## [2.0.0] - 2026-08-16

### Added
- **Two-Layer Architecture:** High-level idiomatic facade (`Xyo.Sdk`) and low-level generated OpenAPI client (`Xyo.Generated`).
- **Core Operations:**
  - `EnrichTransactionAsync`: Real-time synchronous transaction enrichment with merchant metadata, category tags, logo, and address.
  - `EnrichTransactionsAsync`: High-throughput asynchronous bulk batch submission.
  - `GetEnrichmentStatusAsync`: Bulk job lifecycle status polling.
  - `DownloadEnrichmentCollectionAsync`: Memory-safe streaming `.tar.gz` archive download and decompression.
  - `StreamEnrichmentCollectionAsync`: $O(1)$ memory async enumerable streaming for multi-gigabyte archives.
- **Dependency Injection:** First-class `IServiceCollection.AddXyoClient()` with `SocketsHttpHandler` connection pooling and socket lifetime management.
- **Defensive Engineering & Security:**
  - Zero-Trust Egress Domain Validation (CWE-183) with domain pinning and HTTPS enforcement.
  - Decompression bomb protection (CWE-400) enforcing hard stream ceilings.
  - Zip Slip & path traversal defense (CWE-22 / CWE-29).
  - CRLF injection prevention (CWE-113) for `x-api-user` and correlation IDs.
  - Token leakage prevention stripping Bearer headers when accessing external S3 storage hosts.
- **RFC 7807 Exception Hierarchy:** Strongly-typed `XyoProblemDetailsException`, `XyoClientException`, `XyoServerException`, and `XyoNetworkException`.
