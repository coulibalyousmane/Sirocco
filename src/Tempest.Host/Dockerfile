# syntax=docker/dockerfile:1
#
# Contexte de build : la racine du depot (pas ce dossier), car Tempest.Scenarios reference
# ../../protos/tempest_echo.proto en dehors de src/ — voir docker-compose.yml.
#
# Une seule image sert les trois roles (standalone/master/worker) : c'est Tempest:Role, pas
# l'image, qui les distingue. Inutile de construire trois images pour un simple aiguillage
# deja fait par Program.cs.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copie des seuls .csproj d'abord : le cache Docker de RUN dotnet restore ne s'invalide que si
# les references de paquets changent, pas a chaque modification de code source.
COPY Directory.Build.props ./
COPY src/Tempest.Domain/Tempest.Domain.csproj src/Tempest.Domain/
COPY src/Tempest.Application/Tempest.Application.csproj src/Tempest.Application/
COPY src/Tempest.Infrastructure/Tempest.Infrastructure.csproj src/Tempest.Infrastructure/
COPY src/Tempest.Scenarios/Tempest.Scenarios.csproj src/Tempest.Scenarios/
COPY src/Tempest.Host/Tempest.Host.csproj src/Tempest.Host/
RUN dotnet restore src/Tempest.Host/Tempest.Host.csproj

COPY protos/ protos/
COPY src/ src/
RUN dotnet publish src/Tempest.Host/Tempest.Host.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Tempest.Host.dll"]
