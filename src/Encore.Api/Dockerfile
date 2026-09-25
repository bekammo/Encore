# syntax=docker/dockerfile:1

# Runs the Encore host on Linux, for the load harness and the chaos rig. Built in a
# container because Smart App Control blocks freshly built assemblies on the dev machine.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Project files first, restored as their own layer, so source edits do not re-restore.
# Directory.Build.* carry the target framework and the ENCORE rules; Directory.Packages.props
# carries every package version.
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY src/Encore.Api/Encore.Api.csproj                                                 src/Encore.Api/
COPY src/Encore.Shared/Encore.Shared.csproj                                           src/Encore.Shared/
COPY src/Encore.Telemetry/Encore.Telemetry.csproj                                     src/Encore.Telemetry/
COPY src/Encore.Modules.Shared.Persistence/Encore.Modules.Shared.Persistence.csproj    src/Encore.Modules.Shared.Persistence/
COPY src/Encore.Modules.Shared.Http/Encore.Modules.Shared.Http.csproj                  src/Encore.Modules.Shared.Http/
COPY src/Encore.Modules.Catalog/Encore.Modules.Catalog.csproj                         src/Encore.Modules.Catalog/
COPY src/Encore.Modules.Orders/Encore.Modules.Orders.csproj                           src/Encore.Modules.Orders/
COPY src/Encore.Modules.Payments/Encore.Modules.Payments.csproj                       src/Encore.Modules.Payments/
COPY src/Encore.Modules.Notifications/Encore.Modules.Notifications.csproj             src/Encore.Modules.Notifications/
COPY src/Encore.Modules.Catalog.Contracts/Encore.Modules.Catalog.Contracts.csproj     src/Encore.Modules.Catalog.Contracts/
COPY src/Encore.Modules.Payments.Contracts/Encore.Modules.Payments.Contracts.csproj   src/Encore.Modules.Payments.Contracts/
COPY src/Encore.Modules.Inventory/Encore.Modules.Inventory.csproj                     src/Encore.Modules.Inventory/
COPY src/Encore.Modules.Inventory.Domain/Encore.Modules.Inventory.Domain.csproj       src/Encore.Modules.Inventory.Domain/
COPY src/Encore.Modules.Inventory.Contracts/Encore.Modules.Inventory.Contracts.csproj src/Encore.Modules.Inventory.Contracts/

# The host's own graph only; tests have their own image.
RUN dotnet restore src/Encore.Api/Encore.Api.csproj

COPY src/ src/

# -warnaserror to match tests/Dockerfile. Release, because this image is measured.
RUN dotnet publish src/Encore.Api/Encore.Api.csproj \
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

ENTRYPOINT ["dotnet", "Encore.Api.dll"]
