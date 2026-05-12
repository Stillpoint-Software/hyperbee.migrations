#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
ARG NBGV_DISABLE=true
ENV NBGV_DISABLE=${NBGV_DISABLE}

WORKDIR /
COPY ["nuget.config", "/nuget.config"]
COPY ["Directory.Packages.props", "/Directory.Packages.props"]
COPY ["Directory.Build.props", "/Directory.Build.props"]

WORKDIR /src
COPY ["runners/Hyperbee.MigrationRunner.OpenSearch/Hyperbee.MigrationRunner.OpenSearch.csproj", "runners/Hyperbee.MigrationRunner.OpenSearch/"]
COPY ["src/Hyperbee.Migrations.Providers.OpenSearch/Hyperbee.Migrations.Providers.OpenSearch.csproj", "src/Hyperbee.Migrations.Providers.OpenSearch/"]
COPY ["src/Hyperbee.Migrations/Hyperbee.Migrations.csproj", "src/Hyperbee.Migrations/"]

COPY ["runners/samples/Hyperbee.Migrations.OpenSearch.Samples/Hyperbee.Migrations.OpenSearch.Samples.csproj", "runners/samples/Hyperbee.Migrations.OpenSearch.Samples/"]

RUN dotnet restore "./runners/Hyperbee.MigrationRunner.OpenSearch/Hyperbee.MigrationRunner.OpenSearch.csproj"
RUN dotnet restore "./runners/samples/Hyperbee.Migrations.OpenSearch.Samples/Hyperbee.Migrations.OpenSearch.Samples.csproj"

COPY . .

WORKDIR "/src/runners/Hyperbee.MigrationRunner.OpenSearch"
RUN dotnet build "./Hyperbee.MigrationRunner.OpenSearch.csproj" -c $BUILD_CONFIGURATION -f net10.0 -o /app/build

WORKDIR "/src/runners/samples/Hyperbee.Migrations.OpenSearch.Samples"
RUN dotnet build "./Hyperbee.Migrations.OpenSearch.Samples.csproj" -c $BUILD_CONFIGURATION -f net10.0 -o /app/sample_build


FROM build AS publish
ARG BUILD_CONFIGURATION=Release

WORKDIR "/src/runners/Hyperbee.MigrationRunner.OpenSearch"
RUN dotnet publish "./Hyperbee.MigrationRunner.OpenSearch.csproj" -c $BUILD_CONFIGURATION -f net10.0 -o /app/publish /p:UseAppHost=false
WORKDIR "/src/runners/samples/Hyperbee.Migrations.OpenSearch.Samples"
RUN dotnet publish "./Hyperbee.Migrations.OpenSearch.Samples.csproj" -c $BUILD_CONFIGURATION -f net10.0 -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Hyperbee.MigrationRunner.OpenSearch.dll"]
