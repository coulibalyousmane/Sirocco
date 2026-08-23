# syntax=docker/dockerfile:1
#
# Contexte de build : la racine du depot (comme src/Sirocco.Host/Dockerfile), pour beneficier
# du meme cache de restore multi-projets.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Sirocco.Operator/Sirocco.Operator.csproj src/Sirocco.Operator/
RUN dotnet restore src/Sirocco.Operator/Sirocco.Operator.csproj

COPY src/Sirocco.Operator/ src/Sirocco.Operator/
RUN dotnet publish src/Sirocco.Operator/Sirocco.Operator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Sirocco.Operator.dll"]
