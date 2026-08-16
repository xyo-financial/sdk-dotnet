using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xyo.Generated.Model;
using Xyo.Sdk.Client;
using Xyo.Sdk.Exceptions;
using Xyo.Sdk.Extensions;

Console.WriteLine("=================================================");
Console.WriteLine("  XYO Financial .NET SDK - Example Application   ");
Console.WriteLine("=================================================");

string apiKey = Environment.GetEnvironmentVariable("XYO_API_KEY") ?? "xyo_sandbox_token_demo";

// 1. Direct Instantiation
using var directClient = new XyoClient(apiKey);
Console.WriteLine($"Initialized XyoClient instance: {directClient}");

// 2. Dependency Injection Registration
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddXyoClient(options =>
{
    options.ApiKey = apiKey;
    options.CorrelationId = $"trace_{Guid.NewGuid():N}";
});

using var app = builder.Build();
var diClient = app.Services.GetRequiredService<IXyoClient>();

Console.WriteLine("Executing mock enrichment pipeline demonstration...");

try
{
    // Single Transaction Enrichment
    Console.WriteLine("\n[1] Testing Single Transaction Enrichment:");
    var response = await diClient.EnrichTransactionAsync("SQ *COSTA COFFEE GREENWICH", "GB");
    Console.WriteLine($"    Merchant:    {response.Merchant}");
    Console.WriteLine($"    Description: {response.Description}");
    Console.WriteLine($"    Categories:  {string.Join(", ", response.Categories)}");
}
catch (XyoProblemDetailsException ex)
{
    Console.WriteLine($"    Handled RFC 7807 Exception: [{ex.Status}] {ex.Message}");
}
catch (XyoNetworkException ex)
{
    Console.WriteLine($"    Handled Network Exception (Sandbox Offline): {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"    Handled Exception: {ex.Message}");
}

Console.WriteLine("\nExample execution completed successfully.");
