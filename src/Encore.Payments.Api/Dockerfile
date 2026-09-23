# syntax=docker/dockerfile:1

# Runs the Payments service on Linux. Deliberately a copy of the Encore.Api image, not a
# shared base: the two hosts are expected to diverge.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Project files first, restored as their own layer. Only this host's graph.
COPY Directory.Build.props Directory.Build.targets ./
COPY src/Encore.Payments.Api/Encore.Payments.Api.csproj                              src/Encore.Payments.Api/
COPY src/Encore.Shared/Encore.Shared.csproj                                          src/Encore.Shared/
COPY src/Encore.Modules.Shared.Persistence/Encore.Modules.Shared.Persistence.csproj   src/Encore.Modules.Shared.Persistence/
COPY src/Encore.Modules.Payments/Encore.Modules.Payments.csproj                      src/Encore.Modules.Payments/
COPY src/Encore.Modules.Payments.Contracts/Encore.Modules.Payments.Contracts.csproj  src/Encore.Modules.Payments.Contracts/

RUN dotnet restore src/Encore.Payments.Api/Encore.Payments.Api.csproj

COPY src/ src/

RUN dotnet publish src/Encore.Payments.Api/Encore.Payments.Api.csproj \
    --no-restore \
    --configuration Release \
    -warnaserror \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl, for the healthcheck only.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Migrations run before Kestrel opens the socket, so a 200 here means ready.
HEALTHCHECK --interval=2s --timeout=3s --start-period=5s --retries=30 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Encore.Payments.Api.dll"]
