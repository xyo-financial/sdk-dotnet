# XYO Financial SDK for .NET

[![XYO Financial .NET Mascot](https://raw.githubusercontent.com/xyo-financial/sdk-dotnet/main/docs/mascot.png)](https://xyo.financial)

[![CI / Build & Test](https://github.com/xyo-financial/sdk-dotnet/actions/workflows/makefile.yml/badge.svg)](https://github.com/xyo-financial/sdk-dotnet/actions/workflows/makefile.yml)
[![NuGet Package](https://img.shields.io/nuget/v/Xyo.Sdk.svg)](https://www.nuget.org/packages/Xyo.Sdk)
![Target Framework](https://img.shields.io/badge/.NET-8.0%20%7C%209.0-512BD4)
[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://github.com/xyo-financial/sdk-dotnet/blob/main/LICENSE)
[![Security Policy](https://img.shields.io/badge/Security-LTS_Guaranteed-10B981)](https://github.com/xyo-financial/sdk-dotnet/blob/main/SECURITY.md)

**The official .NET SDK for [XYO Financial](https://xyo.financial).**  
Seamlessly enrich raw financial transactions into clean merchant profiles, intelligent business categorizations, high-res logos, and geolocated address metadata using AI-powered enrichment pipelines.

---

## ✨ Key Features

- **⚡ High-Throughput Async/Await:** Pure non-blocking Task-based asynchronous architecture with `CancellationToken` support on all operations.
- **💉 First-Class Dependency Injection:** Seamless ASP.NET Core and Generic Host integration via `services.AddXyoClient()` with `SocketsHttpHandler` socket pooling.
- **🌊 Memory-Safe Batch Streaming:** $O(1)$ memory streaming `.tar.gz` decompression yielding records on-the-fly without buffering multi-gigabyte archives.
- **🛡️ Enterprise Security Hardening:** Zero-Trust domain validation (CWE-183), SSRF defense, Zip Slip mitigation (CWE-22), and decompression bomb limits (CWE-400).
- **📋 RFC 7807 Problem Details:** Rich, typed exception hierarchy mapping API validation errors and HTTP status codes to actionable diagnostics.
- **🔄 Dynamic Token Rotation:** Seamless support for cloud secret stores (Azure Key Vault, AWS Secrets Manager, HashiCorp Vault) via asynchronous key suppliers.

---

## ⚙️ Requirements & Compatibility

| Runtime / Framework | Support Status | Notes |
| :--- | :--- | :--- |
| **.NET 10 LTS** | 🟢 Supported | Next LTS release (forward-compatible) |
| **.NET 9 STS** | 🟢 Supported | Standard-term support runtime |
| **.NET 8 LTS** | 🟡 Minimum Baseline | **Recommended baseline** for institutional production |
| **.NET 6 / .NET Framework** | 🔴 Unsupported | Legacy runtimes reached End of Life |

---

## 📦 Installation

Install the package from [NuGet](https://www.nuget.org/packages/Xyo.Sdk):

### .NET CLI
```bash
dotnet add package Xyo.Sdk
```

### Package Manager Console
```powershell
Install-Package Xyo.Sdk
```

### PackageReference
```xml
<PackageReference Include="Xyo.Sdk" Version="2.1.0" />
```

---

## 🚀 Quickstart Guide

### 1. Direct Client Instantiation

```csharp
using Xyo.Sdk.Client;

// Initialize client with API key
using var client = new XyoClient("xyo_live_your_api_key_here");

// Synchronously enrich a single transaction description
var response = await client.EnrichTransactionAsync("SQ *COSTA COFFEE GREENWICH", "GB");

Console.WriteLine($"Merchant:    {response.Merchant}");
Console.WriteLine($"Description: {response.Description}");
Console.WriteLine($"Categories:  {string.Join(", ", response.Categories)}");
Console.WriteLine($"Logo URL:    {response.Logo}");
```

---

## 📚 Core Operations & Code Examples

### 1. Real-Time Single Transaction Enrichment

```csharp
using Xyo.Generated.Model;
using Xyo.Sdk.Client;

var request = new EnrichmentRequest(
    content: "TFL TRAVEL CHARGE TFL.GOV.UK",
    countryCode: "GB"
);

var response = await client.EnrichTransactionAsync(request);

Console.WriteLine($"Merchant: {response.Merchant}");
Console.WriteLine($"Address:  {response.Address}");
```

### 2. High-Throughput Bulk Batch Submission

Submit batches of transactions for asynchronous parallel processing:

```csharp
var batch = new List<EnrichmentRequest>
{
    new("UBER *TRIP 12345", "GB"),
    new("STARBUCKS STORE #10423", "US"),
    new("AMZN Mktp US*1A2B3C", "US")
};

// Submit batch (optionally attaching tenant user identifier for audit logs)
var batchResponse = await client.EnrichTransactionsAsync(batch, apiUser: "tenant_bank_01");

Console.WriteLine($"Job ID:       {batchResponse.Id}");
Console.WriteLine($"Download URL: {batchResponse.Link}");

// Poll job status
var statusResponse = await client.GetEnrichmentStatusAsync(batchResponse.Id);
Console.WriteLine($"Job Status: {statusResponse.Status}");
```

### 3. Memory-Safe Streaming Batch Download ($O(1)$ Memory)

Stream records on-the-fly directly from the compressed `.tar.gz` archive:

```csharp
// Stream records one-by-one with sub-megabyte memory overhead
await foreach (var record in client.StreamEnrichmentCollectionAsync(batchResponse.Link))
{
    Console.WriteLine($"[Enriched] {record.Merchant} -> {string.Join("/", record.Categories)}");
}
```

Or deserialize the entire archive into a read-only list:

```csharp
IReadOnlyList<EnrichmentResponse> results = await client.DownloadEnrichmentCollectionAsync(batchResponse.Link);
Console.WriteLine($"Downloaded {results.Count} enriched records.");
```

### 3a. Archive Download Timeouts

Archive downloads (`StreamEnrichmentCollectionAsync` / `DownloadEnrichmentCollectionAsync`) are governed by
three independent timeouts, since a single value cannot correctly bound both a connection deadline and a
stall detector:

| Property | Default | Bounds |
| :--- | :--- | :--- |
| `DownloadConnectTimeout` | 10 minutes | Establishing the connection, following redirects, and receiving response headers. |
| `ReadIdleTimeout` | 120 seconds | A single read while streaming the archive body; reset on every read, so a slow but continuous transfer is never treated as a stall. |
| `MaxTotalDownloadDuration` | 1 hour | The cumulative time spent waiting on the network across the whole transfer, so a peer that drips a few bytes just inside every idle window cannot hold the connection open indefinitely. |

None of the three counts time your own code spends processing a yielded record between reads, so a slow
consumer is never penalised.

All three accept `Timeout.InfiniteTimeSpan` to disable the bound explicitly; any other non-positive value
(zero or negative) throws `ArgumentOutOfRangeException` at construction. Disabling `DownloadConnectTimeout`
removes the only bound on the connection/redirect/headers phase, so keep `MaxTotalDownloadDuration` finite if
you disable it. Disabling `ReadIdleTimeout` also silently defeats `MaxTotalDownloadDuration`, since that
budget is only checked once a read returns.

```csharp
var config = new XyoClientConfig("xyo_api_key")
{
    DownloadConnectTimeout = TimeSpan.FromMinutes(2),
    ReadIdleTimeout = TimeSpan.FromSeconds(30),
    MaxTotalDownloadDuration = TimeSpan.FromMinutes(20)
};
```

> **⚠️ Behaviour change:** the previous single `DownloadTimeout` property (still present but obsolete)
> defaulted to 10 minutes and served as both the connection deadline and the read-stall timeout. It is
> superseded by `DownloadConnectTimeout` (10 minutes, unchanged) and `ReadIdleTimeout` (120 seconds). If you
> relied on the old default for stall detection, a peer that previously had 10 minutes to produce the next
> byte is now dropped after 2 minutes. Set `ReadIdleTimeout` explicitly if your environment needs a longer
> allowance. `DownloadTimeout`, when set, still seeds both replacement properties for backward compatibility,
> but is scheduled for removal in the next major version.

### 4. Dynamic Token Rotation (Key Vault / Secrets Manager)

Configure an asynchronous key supplier for zero-downtime secret rotation:

```csharp
var config = new XyoClientConfig()
    .WithTokenSupplier(async (cancellationToken) =>
    {
        // Retrieve fresh token from Azure Key Vault, AWS Secrets Manager, etc.
        return await secretVault.GetSecretAsync("XYO_API_KEY", cancellationToken);
    });

using var dynamicClient = new XyoClient(config);
```

---

## 🚀 Framework & Architecture Integration

The XYO Financial .NET SDK is engineered for institutional-grade reliability, high-concurrency microservices, and cloud-native deployments across .NET 8, .NET 9, and upcoming .NET 10 LTS runtimes.

### 1. ASP.NET Core 8 / 9 Integration with Polly Resilience

Register `IXyoClient` in `Program.cs` using the official dependency injection extension methods with Microsoft Polly transient error retry policies and circuit breakers:

```csharp
// ASP.NET Core: Program.cs
using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Xyo.Generated.Model;
using Xyo.Sdk.Client;
using Xyo.Sdk.Exceptions;
using Xyo.Sdk.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register XYO Client with configuration options and Polly retry policies
builder.Services.AddXyoClient(options => 
{
    options.ApiKey = builder.Configuration["Xyo:ApiKey"]!;
    options.BaseUrl = builder.Configuration["Xyo:BaseUrl"] ?? "https://api.xyo.financial";
    options.Timeout = TimeSpan.FromSeconds(2);
    options.CorrelationId = "banking-api-gateway";
})
.AddTransientHttpErrorPolicy(policy => 
    policy.WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromMilliseconds(200 * retryAttempt)));

var app = builder.Build();

// High-performance Minimal API endpoint
app.MapPost("/api/v1/enrich", async (EnrichmentDto dto, IXyoClient xyoClient, CancellationToken ct) =>
{
    try
    {
        var response = await xyoClient.EnrichTransactionAsync(dto.Description, dto.CountryCode, ct);
        return Results.Ok(response);
    }
    catch (XyoProblemDetailsException ex)
    {
        return Results.Problem(
            title: ex.Title,
            detail: ex.Detail,
            statusCode: ex.Status,
            type: ex.Type,
            extensions: new Dictionary<string, object?> { ["errors"] = ex.Errors }
        );
    }
});

app.Run();

public record EnrichmentDto(string Description, string CountryCode);
```

#### Configuration Reload Behaviour

`IXyoClient` is registered as a singleton, and stays a singleton -- this is deliberate (an earlier `AddTransient` registration leaked, because `IXyoClient` is `IDisposable` and the container captured every instance resolved from the root provider). Despite that, the resolved client **does** observe later changes to `XyoClientOptions`, for example a reload of the `appsettings.json` section it is bound from. `AddXyoClient` resolves `IOptionsMonitor<XyoClientOptions>` rather than a one-shot `IOptions<XyoClientOptions>`, and rebuilds its effective configuration only when the change token fires, never per call, so there is no cost added to the request hot path. A reload that fails validation (for example an invalid `BaseUrl`) is rejected: the client keeps serving requests with its last valid configuration, and the failure is logged rather than swallowed or allowed to crash the process.

This reload behaviour is specific to the `Action<XyoClientOptions>` overload (and the `string apiKey` overload, which delegates to it). The `AddXyoClient(XyoClientConfig)` overload takes an explicit, already-built configuration with no options source behind it, so it is read once and fixed for the lifetime of the registration -- there is nothing for it to reload from.

### 2. High-Performance Architectural Highlights

| Architecture Dimension | Implementation Mechanism | Enterprise Benefit |
| :--- | :--- | :--- |
| **Native AOT Compatibility** | Trim-safe and AOT-compliant JSON serialization with zero reflection on critical execution paths. | Instant cold starts (<15ms), reduced memory footprint, and smaller container images for AWS ECS, Google Cloud Run, and Kubernetes. |
| **Zero LOH Allocations** | $O(1)$ streaming decompression (`StreamEnrichmentCollectionAsync`) yielding records directly via `Span<byte>` and bounded streams. | Eliminates Large Object Heap (LOH) pressure and prevents Gen 2 garbage collection pauses during multi-gigabyte batch processing. |
| **Pooled Socket Management** | Underlying `SocketsHttpHandler` with `PooledConnectionLifetime` (15m) and infinite handler lifetime. | Eliminates socket exhaustion under high throughput while respecting DNS rotation and TLS renegotiation. |

#### Pooled `SocketsHttpHandler` Socket Lifetime Management
When registering via `AddXyoClient`, the SDK configures a pooled `SocketsHttpHandler` instance with `PooledConnectionLifetime = TimeSpan.FromMinutes(15)` and `SetHandlerLifetime(Timeout.InfiniteTimeSpan)`. This follows modern .NET networking best practices, preventing socket exhaustion (`TIME_WAIT` proliferation) during burst traffic while ensuring DNS record updates are respected without recycling handler instances.

#### Zero Large Object Heap (LOH) Allocations
Traditional batch processing reads entire multi-gigabyte compressed `.tar.gz` archives into memory buffers, causing objects $\ge 85,000$ bytes to be allocated directly onto the Large Object Heap (LOH). The XYO SDK utilizes a streaming push/pull decompression architecture that reads records chunk-by-chunk directly into pooled memory buffers, maintaining strict $O(1)$ memory consumption regardless of whether the archive contains 100 or 10,000,000 transactions.

#### Native AOT (Ahead-Of-Time) Compilation
The SDK is fully compatible with .NET Native AOT publishing (`PublishAot=true`). By avoiding dynamic runtime code emission and untyped reflection, binaries compile directly into native machine code for maximum security and execution speed:

```bash
dotnet publish -c Release -r linux-x64 --self-contained
```

### 3. Enterprise Background Worker Service (`BackgroundService`)

For continuous batch processing and automated reconciliation pipelines, inject `IXyoClient` inside an `IHostedService` / `BackgroundService`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xyo.Sdk.Client;

public sealed class TransactionBatchWorker : BackgroundService
{
    private readonly IXyoClient _xyoClient;
    private readonly ILogger<TransactionBatchWorker> _logger;

    public TransactionBatchWorker(IXyoClient xyoClient, ILogger<TransactionBatchWorker> logger)
    {
        _xyoClient = xyoClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Ingest archive stream on-the-fly with O(1) memory overhead
                string downloadUrl = "https://download.xyo.financial/batches/reconciliation_2026_08.tar.gz";
                
                await foreach (var record in _xyoClient.StreamEnrichmentCollectionAsync(downloadUrl, stoppingToken))
                {
                    _logger.LogInformation("Processed transaction {Merchant} ({Category})", 
                        record.Merchant, string.Join(", ", record.Categories));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Transient batch ingestion failure occurred. Retrying...");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
```

---

## 🛡 Exception Handling & RFC 7807 Problem Details

The SDK throws strongly-typed exceptions conforming to the RFC 7807 Problem Details specification:

```csharp
using Xyo.Sdk.Exceptions;

try
{
    var result = await client.EnrichTransactionAsync("COSTA", "INVALID_CODE");
}
catch (XyoProblemDetailsException ex)
{
    // RFC 7807 Structured Problem Details
    Console.WriteLine($"HTTP Status: {ex.Status}");
    Console.WriteLine($"Title:       {ex.Title}");
    Console.WriteLine($"Detail:      {ex.Detail}");
    Console.WriteLine($"Type:        {ex.Type}");

    if (ex.Errors != null)
    {
        foreach (var (field, errors) in ex.Errors)
        {
            Console.WriteLine($"  Field '{field}': {string.Join(", ", errors)}");
        }
    }
}
catch (XyoServerException ex) when (ex.IsRetryable())
{
    // Transient 5xx server errors safe for exponential backoff
    Console.WriteLine($"Transient server error: {ex.StatusCode}. Retrying...");
}
catch (XyoNetworkException ex)
{
    // Transport errors (DNS, timeout, connection resets)
    Console.WriteLine($"Network failure: {ex.Message}");
}
```

---

## 🔒 Security & Defensive Architecture

- **Zero-Trust Domain Egress Allowlist:** Validates all archive download URLs against pinned official domains (`api.xyo.financial`, `download.xyo.financial`, AWS S3 storage hosts) and strictly rejects cleartext HTTP.
- **Credential Leakage Prevention:** Strips Bearer authorization headers when following download links to third-party or S3 storage buckets.
- **Decompression Bomb Defense (CWE-400):** Streaming Tar and GZip decoders enforce hard limits on total archive byte ingestion, per-entry sizes, and entry counts.
- **Zip Slip Defense (CWE-22):** Rejects directory traversal sequences and rooted paths in archive entry names.
- **CRLF Injection Prevention (CWE-113):** Validates custom headers and user IDs against carriage return and newline characters.

---

## 📄 License

Distributed under the **Apache License, Version 2.0**. See [`LICENSE`](LICENSE) for details.

---

## 🤝 Support & Security

- **Documentation & Portal:** [xyo.financial](https://xyo.financial)
- **Security Policy & Vulnerability Reporting:** [`SECURITY.md`](SECURITY.md) / `security@syniol.com`
- **Contribution Guidelines:** [`CONTRIBUTING.md`](CONTRIBUTING.md)
