#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["samples/Hyperbee.MigrationRunner.MongoDB/Hyperbee.MigrationRunner.MongoDB.csproj", "samples/Hyperbee.MigrationRunner.MongoDB/"]
COPY ["src/Hyperbee.Migrations.Providers.MongoDB/Hyperbee.Migrations.Providers.MongoDB.csproj", "src/Hyperbee.Migrations.Providers.MongoDB/"]
COPY ["src/Hyperbee.Migrations/Hyperbee.Migrations.csproj", "src/Hyperbee.Migrations/"]

COPY ["samples/Hyperbee.Migrations.MongoDB.Samples/Hyperbee.Migrations.MongoDB.Samples.csproj", "samples/Hyperbee.Migrations.MongoDB.Samples/"]

RUN dotnet restore "./samples/Hyperbee.MigrationRunner.MongoDB/Hyperbee.MigrationRunner.MongoDB.csproj"
RUN dotnet restore "./samples/Hyperbee.Migrations.MongoDB.Samples/Hyperbee.Migrations.MongoDB.Samples.csproj"

COPY . .

WORKDIR "/src/samples/Hyperbee.MigrationRunner.MongoDB"
RUN dotnet build "./Hyperbee.MigrationRunner.MongoDB.csproj" -c $BUILD_CONFIGURATION -o /app/build

WORKDIR "/src/samples/Hyperbee.Migrations.MongoDB.Samples"
RUN dotnet build "./Hyperbee.Migrations.MongoDB.Samples.csproj" -c $BUILD_CONFIGURATION -o /app/sample_build


FROM build AS publish
ARG BUILD_CONFIGURATION=Release

WORKDIR "/src/samples/Hyperbee.MigrationRunner.MongoDB"
RUN dotnet publish "./Hyperbee.MigrationRunner.MongoDB.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
WORKDIR "/src/samples/Hyperbee.Migrations.MongoDB.Samples"
RUN dotnet publish "./Hyperbee.Migrations.MongoDB.Samples.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Hyperbee.MigrationRunner.MongoDB.dll"]