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
- `AddXyoClient(Action<XyoClientOptions>)` now honours live `XyoClientOptions` reloads (e.g. `appsettings.json` changes) on the already-resolved singleton `IXyoClient`, via `IOptionsMonitor<XyoClientOptions>`. The registered lifetime stays `Singleton`; a reload is only rebuilt on its change token, never per call, and a reload that fails validation is rejected, leaving the client on its last valid configuration and logging the failure rather than swallowing it (US-DOTNET-004).

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
