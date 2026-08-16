# XYO.Financial SDK for .NET

<p align="center">
    <a href="https://xyo.financial" target="_blank"><img alt=".NET Bot Mascot for XYO.Financial" width="420" src="docs/mascot.png" /></a>
    <br/>
    <b>Enterprise Financial Transaction Enrichment SDK for .NET 8.0 & 9.0</b>
</p>

<p align="center">
    <a href="https://github.com/xyo-financial/sdk-dotnet/actions/workflows/makefile.yml"><img src="https://github.com/xyo-financial/sdk-dotnet/actions/workflows/makefile.yml/badge.svg" alt="CI / Build & Test" /></a>
    <a href="https://www.nuget.org/packages/Xyo.Sdk"><img src="https://img.shields.io/nuget/v/Xyo.Sdk.svg" alt="NuGet Package" /></a>
    <img src="https://img.shields.io/badge/.NET-8.0%20%7C%209.0-512BD4" alt="Target Framework" />
    <a href="LICENSE"><img src="https://img.shields.io/badge/License-Apache_2.0-blue.svg" alt="License" /></a>
    <a href="SECURITY.md"><img src="https://img.shields.io/badge/Security-LTS_Guaranteed-10B981" alt="Security Policy" /></a>
</p>

Official, institutional-grade .NET Client SDK for the **[XYO Financial](https://xyo.financial)** AI Transaction Enrichment Platform. Built for Tier-1 banks, payment processors, and fintech platforms demanding high throughput, non-blocking asynchronous I/O, zero memory leaks, and enterprise-grade resilience.

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
<PackageReference Include="Xyo.Sdk" Version="2.0.0" />
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

## 🏗 Dependency Injection (ASP.NET Core & Generic Host)

Register `IXyoClient` in `Program.cs` using the official DI extension methods:

```csharp
using Xyo.Sdk.Client;
using Xyo.Sdk.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register XYO Client with configuration options
builder.Services.AddXyoClient(options =>
{
    options.ApiKey = builder.Configuration["Xyo:ApiKey"];
    options.BaseUrl = builder.Configuration["Xyo:BaseUrl"] ?? "https://api.xyo.financial";
    options.Timeout = TimeSpan.FromSeconds(30);
    options.CorrelationId = "banking-api-gateway";
});

var app = builder.Build();

// Inject IXyoClient into minimal APIs, controllers, or background services
app.MapPost("/api/enrich", async (EnrichmentDto dto, IXyoClient xyoClient, CancellationToken ct) =>
{
    var enriched = await xyoClient.EnrichTransactionAsync(dto.Description, dto.CountryCode, ct);
    return Results.Ok(enriched);
});

app.Run();
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

## 🔒 Enterprise Security & Defensive Architecture

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
