# syntax=docker/dockerfile:1

# Runs the Payments service on Linux. DECISIONS 061.
#
# The same shape as src/Encore.Api/Dockerfile, and deliberately not a shared base
# image: two hosts that build identically today will not stay identical, and a
# base image is the kind of coupling that makes the second one's first divergence
# expensive. Forty duplicated lines is the cheaper side of that trade, on 017's
# reasoning — this is inert build plumbing, and a bug in one copy cannot be a bug
# in the other.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Project files first, restored as their own layer, so editing source rebuilds
# without re-resolving the package graph. Only this host's graph is copied: the
# module, its contracts and the shared persistence block.
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

# curl for the healthcheck below and nothing else; the runtime image ships with
# neither curl nor wget.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# /health is this host's own route, so a 200 means the pipeline is serving. The
# migrator runs as IHostedLifecycleService and finishes before Kestrel opens the
# socket, which is what makes this readiness rather than just liveness (013).
HEALTHCHECK --interval=2s --timeout=3s --start-period=5s --retries=30 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Encore.Payments.Api.dll"]
