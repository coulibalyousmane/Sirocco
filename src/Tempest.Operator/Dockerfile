# syntax=docker/dockerfile:1
#
# Contexte de build : la racine du depot (comme src/Tempest.Host/Dockerfile), pour beneficier
# du meme cache de restore multi-projets.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Tempest.Operator/Tempest.Operator.csproj src/Tempest.Operator/
RUN dotnet restore src/Tempest.Operator/Tempest.Operator.csproj

COPY src/Tempest.Operator/ src/Tempest.Operator/
RUN dotnet publish src/Tempest.Operator/Tempest.Operator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Tempest.Operator.dll"]
