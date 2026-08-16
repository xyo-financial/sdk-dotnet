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
