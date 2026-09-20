# syntax=docker/dockerfile:1

# Runs the Encore host on Linux.
#
# It exists so the API can be load-tested, which needs something a harness can
# point at and something the harness can be run beside. The same reasoning as
# tests/Dockerfile applies to the build stage: Smart App Control on the
# development machine blocks freshly compiled unsigned assemblies and has no
# exclusion mechanism, so anything built to be *run* here is built in a
# container. CI and a later deploy both want this image too, which is why it is
# a real multi-stage build rather than an SDK image with `dotnet run` in it.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Project files first, restored as their own layer, exactly as tests/Dockerfile
# does it and for the same reason: editing source rebuilds without re-resolving
# the package graph. Directory.Build.props sets the target framework and
# Directory.Build.targets carries ENCORE001-003, so both have to arrive before
# any csproj is evaluated or the rules would not be in force in here.
COPY Directory.Build.props Directory.Build.targets ./
COPY src/Encore.Api/Encore.Api.csproj                                                 src/Encore.Api/
COPY src/Encore.Shared/Encore.Shared.csproj                                           src/Encore.Shared/
COPY src/Encore.Modules.Catalog/Encore.Modules.Catalog.csproj                         src/Encore.Modules.Catalog/
COPY src/Encore.Modules.Orders/Encore.Modules.Orders.csproj                           src/Encore.Modules.Orders/
COPY src/Encore.Modules.Payments/Encore.Modules.Payments.csproj                       src/Encore.Modules.Payments/
COPY src/Encore.Modules.Notifications/Encore.Modules.Notifications.csproj             src/Encore.Modules.Notifications/
COPY src/Encore.Modules.Catalog.Contracts/Encore.Modules.Catalog.Contracts.csproj     src/Encore.Modules.Catalog.Contracts/
COPY src/Encore.Modules.Payments.Contracts/Encore.Modules.Payments.Contracts.csproj   src/Encore.Modules.Payments.Contracts/
COPY src/Encore.Modules.Inventory/Encore.Modules.Inventory.csproj                     src/Encore.Modules.Inventory/
COPY src/Encore.Modules.Inventory.Domain/Encore.Modules.Inventory.Domain.csproj       src/Encore.Modules.Inventory.Domain/
COPY src/Encore.Modules.Inventory.Contracts/Encore.Modules.Inventory.Contracts.csproj src/Encore.Modules.Inventory.Contracts/

# The host's own graph only. The test projects are not copied and not restored;
# they have their own image.
RUN dotnet restore src/Encore.Api/Encore.Api.csproj

COPY src/ src/

# -warnaserror to match tests/Dockerfile: a warning that only one of the two
# images rejects is a warning nobody sees. Release because this image is meant
# to be measured, and measuring a Debug build would answer a question nobody
# asked.
RUN dotnet publish src/Encore.Api/Encore.Api.csproj \
    --no-restore \
    --configuration Release \
    -warnaserror \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is here for the healthcheck below and for nothing else. The runtime image
# is Debian slim and ships with neither curl nor wget, and a compose healthcheck
# is what makes `--wait` mean "ready to be measured" rather than "started".
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# The aspnet image already defaults ASPNETCORE_HTTP_PORTS to 8080; saying it
# here means the compose port mapping and this file cannot drift apart silently.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# /health is the host's own route (Program.cs), not a framework health check, so
# a 200 from it means the pipeline is actually serving. The migrators run as
# IHostedLifecycleService and therefore finish before Kestrel opens the socket,
# which is what makes this a sufficient readiness signal and not just a liveness
# one -- see DECISIONS 013.
HEALTHCHECK --interval=2s --timeout=3s --start-period=5s --retries=30 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Encore.Api.dll"]
