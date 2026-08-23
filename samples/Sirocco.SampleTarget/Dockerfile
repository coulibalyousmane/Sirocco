# syntax=docker/dockerfile:1
#
# Contexte de build : la racine du depot (pas ce dossier), car ce projet reference
# ../../protos/sirocco_echo.proto en dehors de samples/ — voir docker-compose.yml.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY samples/Sirocco.SampleTarget/Sirocco.SampleTarget.csproj samples/Sirocco.SampleTarget/
RUN dotnet restore samples/Sirocco.SampleTarget/Sirocco.SampleTarget.csproj

COPY protos/ protos/
COPY samples/Sirocco.SampleTarget/ samples/Sirocco.SampleTarget/
RUN dotnet publish samples/Sirocco.SampleTarget/Sirocco.SampleTarget.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Sirocco.SampleTarget.dll"]
