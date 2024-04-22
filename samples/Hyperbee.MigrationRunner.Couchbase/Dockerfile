#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["samples/Hyperbee.MigrationRunner.Couchbase/Hyperbee.MigrationRunner.Couchbase.csproj", "samples/Hyperbee.MigrationRunner.Couchbase/"]
COPY ["src/Hyperbee.Migrations.Providers.Couchbase/Hyperbee.Migrations.Providers.Couchbase.csproj", "src/Hyperbee.Migrations.Providers.Couchbase/"]
COPY ["src/Hyperbee.Migrations/Hyperbee.Migrations.csproj", "src/Hyperbee.Migrations/"]

COPY ["samples/Hyperbee.Migrations.Couchbase.Samples/Hyperbee.Migrations.Couchbase.Samples.csproj", "samples/Hyperbee.Migrations.Couchbase.Samples/"]

RUN dotnet restore "./samples/Hyperbee.MigrationRunner.Couchbase/Hyperbee.MigrationRunner.Couchbase.csproj"
RUN dotnet restore "./samples/Hyperbee.Migrations.Couchbase.Samples/Hyperbee.Migrations.Couchbase.Samples.csproj"

COPY . .

WORKDIR "/src/samples/Hyperbee.MigrationRunner.Couchbase"
RUN dotnet build "./Hyperbee.MigrationRunner.Couchbase.csproj" -c $BUILD_CONFIGURATION -o /app/build

WORKDIR "/src/samples/Hyperbee.Migrations.Couchbase.Samples"
RUN dotnet build "./Hyperbee.Migrations.Couchbase.Samples.csproj" -c $BUILD_CONFIGURATION -o /app/sample_build


FROM build AS publish
ARG BUILD_CONFIGURATION=Release

WORKDIR "/src/samples/Hyperbee.MigrationRunner.Couchbase"
RUN dotnet publish "./Hyperbee.MigrationRunner.Couchbase.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
WORKDIR "/src/samples/Hyperbee.Migrations.Couchbase.Samples"
RUN dotnet publish "./Hyperbee.Migrations.Couchbase.Samples.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Hyperbee.MigrationRunner.Couchbase.dll"]