# syntax=docker/dockerfile:1
#
# Contexte de build : la racine du depot (pas ce dossier), car ce projet reference
# ../../protos/tempest_echo.proto en dehors de samples/ — voir docker-compose.yml.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY samples/Tempest.SampleTarget/Tempest.SampleTarget.csproj samples/Tempest.SampleTarget/
RUN dotnet restore samples/Tempest.SampleTarget/Tempest.SampleTarget.csproj

COPY protos/ protos/
COPY samples/Tempest.SampleTarget/ samples/Tempest.SampleTarget/
RUN dotnet publish samples/Tempest.SampleTarget/Tempest.SampleTarget.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Tempest.SampleTarget.dll"]
