#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Hyperbee.MigrationRunner/Hyperbee.MigrationRunner.csproj", "src/Hyperbee.MigrationRunner/"]
COPY ["src/Hyperbee.Migrations.Providers.MongoDB/Hyperbee.Migrations.Providers.MongoDB.csproj", "src/Hyperbee.Migrations.Providers.MongoDB/"]
COPY ["src/Hyperbee.Migrations/Hyperbee.Migrations.csproj", "src/Hyperbee.Migrations/"]

COPY ["samples/Hyperbee.Migrations.MongoDB.Samples/Hyperbee.Migrations.MongoDB.Samples.csproj", "samples/Hyperbee.Migrations.MongoDB.Samples/"]

RUN dotnet restore "./src/Hyperbee.MigrationRunner/Hyperbee.MigrationRunner.csproj"
RUN dotnet restore "./samples/Hyperbee.Migrations.MongoDB.Samples/Hyperbee.Migrations.MongoDB.Samples.csproj"

COPY . .

WORKDIR "/src/src/Hyperbee.MigrationRunner"
RUN dotnet build "./Hyperbee.MigrationRunner.csproj" -c $BUILD_CONFIGURATION -o /app/build

WORKDIR "/src/samples/Hyperbee.Migrations.MongoDB.Samples"
RUN dotnet build "./Hyperbee.Migrations.MongoDB.Samples.csproj" -c $BUILD_CONFIGURATION -o /app/sample_build


FROM build AS publish
ARG BUILD_CONFIGURATION=Release

WORKDIR "/src/src/Hyperbee.MigrationRunner"
RUN dotnet publish "./Hyperbee.MigrationRunner.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
WORKDIR "/src/samples/Hyperbee.Migrations.MongoDB.Samples"
RUN dotnet publish "./Hyperbee.Migrations.MongoDB.Samples.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Hyperbee.MigrationRunner.dll"]