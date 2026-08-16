# ==========================================
# Multi-stage Dockerfile for .NET SDK
# ==========================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy solution and project definitions
COPY Xyo.Sdk.sln ./
COPY src/Xyo.Generated/Xyo.Generated.csproj ./src/Xyo.Generated/
COPY src/Xyo.Sdk/Xyo.Sdk.csproj ./src/Xyo.Sdk/
COPY tests/Xyo.Sdk.Tests/Xyo.Sdk.Tests.csproj ./tests/Xyo.Sdk.Tests/
COPY example/Example.csproj ./example/

# Restore dependencies
RUN dotnet restore Xyo.Sdk.sln

# Copy source code
COPY src/ ./src/
COPY tests/ ./tests/
COPY example/ ./example/
COPY README.md ./

# Build solution
RUN dotnet build Xyo.Sdk.sln -c Release --no-restore

# Run unit and integration tests
FROM build AS test
WORKDIR /app
RUN dotnet test Xyo.Sdk.sln -c Release --no-build --verbosity normal --collect:"XPlat Code Coverage"

# Package artifacts
FROM build AS pack
WORKDIR /app
RUN dotnet pack src/Xyo.Sdk/Xyo.Sdk.csproj -c Release --no-build -o /app/packages

# Final runtime image for example execution
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/example/bin/Release/net8.0/ ./
ENTRYPOINT ["dotnet", "Example.dll"]
