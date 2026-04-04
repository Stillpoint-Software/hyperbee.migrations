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
COPY ["Directory.Build.targets", "/Directory.Build.targets"]

WORKDIR /src
COPY ["samples/Hyperbee.MigrationRunner.Aerospike/Hyperbee.MigrationRunner.Aerospike.csproj", "samples/Hyperbee.MigrationRunner.Aerospike/"]
COPY ["src/Hyperbee.Migrations.Providers.Aerospike/Hyperbee.Migrations.Providers.Aerospike.csproj", "src/Hyperbee.Migrations.Providers.Aerospike/"]
COPY ["src/Hyperbee.Migrations/Hyperbee.Migrations.csproj", "src/Hyperbee.Migrations/"]

COPY ["samples/Hyperbee.Migrations.Aerospike.Samples/Hyperbee.Migrations.Aerospike.Samples.csproj", "samples/Hyperbee.Migrations.Aerospike.Samples/"]

RUN dotnet restore "./samples/Hyperbee.MigrationRunner.Aerospike/Hyperbee.MigrationRunner.Aerospike.csproj"
RUN dotnet restore "./samples/Hyperbee.Migrations.Aerospike.Samples/Hyperbee.Migrations.Aerospike.Samples.csproj"

COPY . .

WORKDIR "/src/samples/Hyperbee.MigrationRunner.Aerospike"
RUN dotnet build "./Hyperbee.MigrationRunner.Aerospike.csproj" -c $BUILD_CONFIGURATION -o /app/build

WORKDIR "/src/samples/Hyperbee.Migrations.Aerospike.Samples"
RUN dotnet build "./Hyperbee.Migrations.Aerospike.Samples.csproj" -c $BUILD_CONFIGURATION -o /app/sample_build


FROM build AS publish
ARG BUILD_CONFIGURATION=Release

WORKDIR "/src/samples/Hyperbee.MigrationRunner.Aerospike"
RUN dotnet publish "./Hyperbee.MigrationRunner.Aerospike.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
WORKDIR "/src/samples/Hyperbee.Migrations.Aerospike.Samples"
RUN dotnet publish "./Hyperbee.Migrations.Aerospike.Samples.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Hyperbee.MigrationRunner.Aerospike.dll"]
