#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app
# Install required runtime dependency for Npgsql to enable GSSAPI/SSPI
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
# Disable Nerdbank.GitVersioning inside Docker builds
ENV NBGV_DisableVersioning=true

WORKDIR /
COPY ["nuget.config", "/nuget.config"]
COPY ["Directory.Packages.props", "/Directory.Packages.props"]
COPY ["Directory.Build.props", "/Directory.Build.props"]
COPY ["Directory.Build.targets", "/Directory.Build.targets"]

WORKDIR /src
COPY ["samples/Hyperbee.MigrationRunner.Postgres/Hyperbee.MigrationRunner.Postgres.csproj", "samples/Hyperbee.MigrationRunner.Postgres/"]
COPY ["src/Hyperbee.Migrations.Providers.Postgres/Hyperbee.Migrations.Providers.Postgres.csproj", "src/Hyperbee.Migrations.Providers.Postgres/"]
COPY ["src/Hyperbee.Migrations/Hyperbee.Migrations.csproj", "src/Hyperbee.Migrations/"]

COPY ["samples/Hyperbee.Migrations.Postgres.Samples/Hyperbee.Migrations.Postgres.Samples.csproj", "samples/Hyperbee.Migrations.Postgres.Samples/"]

RUN dotnet restore "./samples/Hyperbee.MigrationRunner.Postgres/Hyperbee.MigrationRunner.Postgres.csproj"
RUN dotnet restore "./samples/Hyperbee.Migrations.Postgres.Samples/Hyperbee.Migrations.Postgres.Samples.csproj"

COPY . .

WORKDIR "/src/samples/Hyperbee.MigrationRunner.Postgres"
RUN dotnet build "./Hyperbee.MigrationRunner.Postgres.csproj" -c $BUILD_CONFIGURATION -o /app/build 

WORKDIR "/src/samples/Hyperbee.Migrations.Postgres.Samples"
RUN dotnet build "./Hyperbee.Migrations.Postgres.Samples.csproj" -c $BUILD_CONFIGURATION -o /app/sample_build


FROM build AS publish
ARG BUILD_CONFIGURATION=Release

WORKDIR "/src/samples/Hyperbee.MigrationRunner.Postgres"
RUN dotnet publish "./Hyperbee.MigrationRunner.Postgres.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
WORKDIR "/src/samples/Hyperbee.Migrations.Postgres.Samples"
RUN dotnet publish "./Hyperbee.Migrations.Postgres.Samples.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Hyperbee.MigrationRunner.Postgres.dll"]