# syntax=docker/dockerfile:1
#
# Contexte de build : la racine du depot (pas ce dossier), car Sirocco.Scenarios reference
# ../../protos/sirocco_echo.proto en dehors de src/ — voir docker-compose.yml.
#
# Une seule image sert les trois roles (standalone/master/worker) : c'est Sirocco:Role, pas
# l'image, qui les distingue. Inutile de construire trois images pour un simple aiguillage
# deja fait par Program.cs.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copie des seuls .csproj d'abord : le cache Docker de RUN dotnet restore ne s'invalide que si
# les references de paquets changent, pas a chaque modification de code source.
COPY Directory.Build.props ./
COPY src/Sirocco.Domain/Sirocco.Domain.csproj src/Sirocco.Domain/
COPY src/Sirocco.Application/Sirocco.Application.csproj src/Sirocco.Application/
COPY src/Sirocco.Infrastructure/Sirocco.Infrastructure.csproj src/Sirocco.Infrastructure/
COPY src/Sirocco.Scenarios/Sirocco.Scenarios.csproj src/Sirocco.Scenarios/
COPY src/Sirocco.Host/Sirocco.Host.csproj src/Sirocco.Host/
RUN dotnet restore src/Sirocco.Host/Sirocco.Host.csproj

COPY protos/ protos/
COPY src/ src/
RUN dotnet publish src/Sirocco.Host/Sirocco.Host.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Sirocco.Host.dll"]
