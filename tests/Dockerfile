# syntax=docker/dockerfile:1

# Runs Encore's test suite on Linux.
#
# This exists because of a host problem, not a design one. Windows Smart App
# Control is a Code Integrity policy in enforcement mode: it allows code that is
# either signed by a publisher it trusts or vouched for by Microsoft's cloud
# reputation service, and a freshly compiled assembly is neither. When the
# reputation lookup returns nothing, the default is deny, so `dotnet test` on the
# host intermittently fails to load Encore.Shared.dll with 0x800711C7 — a
# FileLoadException that looks like a broken build and is not one. Rebuilding
# cannot help: reputation is keyed on file hash, so every rebuild asks a question
# that is not being answered. Smart App Control has no exclusion mechanism.
#
# A Linux container is not subject to that policy, which is the whole trick. The
# side benefit is the one worth keeping: the suite now runs identically here and
# on any machine that has Docker, without anyone first discovering this.
FROM mcr.microsoft.com/dotnet/sdk:10.0

WORKDIR /src

# Project files first, restored as their own layer. Editing source then rebuilds
# without re-resolving the package graph, which is the difference between a
# ten-second loop and a two-minute one.
COPY Directory.Build.props Directory.Build.targets Encore.sln ./
COPY src/Encore.Api/Encore.Api.csproj                                          src/Encore.Api/
COPY src/Encore.Shared/Encore.Shared.csproj                                    src/Encore.Shared/
COPY src/Encore.Modules.Shared.Persistence/Encore.Modules.Shared.Persistence.csproj src/Encore.Modules.Shared.Persistence/
COPY src/Encore.Modules.Catalog/Encore.Modules.Catalog.csproj                  src/Encore.Modules.Catalog/
COPY src/Encore.Modules.Orders/Encore.Modules.Orders.csproj                    src/Encore.Modules.Orders/
COPY src/Encore.Modules.Payments/Encore.Modules.Payments.csproj                src/Encore.Modules.Payments/
COPY src/Encore.Modules.Notifications/Encore.Modules.Notifications.csproj      src/Encore.Modules.Notifications/
COPY src/Encore.Modules.Catalog.Contracts/Encore.Modules.Catalog.Contracts.csproj      src/Encore.Modules.Catalog.Contracts/
COPY src/Encore.Modules.Payments.Contracts/Encore.Modules.Payments.Contracts.csproj    src/Encore.Modules.Payments.Contracts/
COPY src/Encore.Modules.Inventory/Encore.Modules.Inventory.csproj              src/Encore.Modules.Inventory/
COPY src/Encore.Modules.Inventory.Domain/Encore.Modules.Inventory.Domain.csproj src/Encore.Modules.Inventory.Domain/
COPY src/Encore.Modules.Inventory.Contracts/Encore.Modules.Inventory.Contracts.csproj src/Encore.Modules.Inventory.Contracts/
COPY tests/Encore.Modules.Catalog.IntegrationTests/Encore.Modules.Catalog.IntegrationTests.csproj tests/Encore.Modules.Catalog.IntegrationTests/
COPY tests/Encore.Modules.Orders.IntegrationTests/Encore.Modules.Orders.IntegrationTests.csproj   tests/Encore.Modules.Orders.IntegrationTests/
COPY tests/Encore.Modules.Orders.UnitTests/Encore.Modules.Orders.UnitTests.csproj                 tests/Encore.Modules.Orders.UnitTests/
COPY tests/Encore.Modules.Inventory.UnitTests/Encore.Modules.Inventory.UnitTests.csproj               tests/Encore.Modules.Inventory.UnitTests/
COPY tests/Encore.Modules.Inventory.IntegrationTests/Encore.Modules.Inventory.IntegrationTests.csproj tests/Encore.Modules.Inventory.IntegrationTests/
COPY tests/Encore.Modules.Payments.UnitTests/Encore.Modules.Payments.UnitTests.csproj                 tests/Encore.Modules.Payments.UnitTests/
COPY tests/Encore.Modules.Payments.IntegrationTests/Encore.Modules.Payments.IntegrationTests.csproj   tests/Encore.Modules.Payments.IntegrationTests/
COPY tests/Encore.Modules.Notifications.IntegrationTests/Encore.Modules.Notifications.IntegrationTests.csproj tests/Encore.Modules.Notifications.IntegrationTests/
COPY tests/Encore.ArchitectureTests/Encore.ArchitectureTests.csproj                                  tests/Encore.ArchitectureTests/

RUN dotnet restore

COPY src/ src/
COPY tests/ tests/

# The decision log is a test input, not documentation, since DECISIONS 060: the
# suite reads it and compares its index against its headings. Last, and on its
# own line, because it changes on almost every commit and this is the cheapest
# layer in the file to invalidate.
COPY DECISIONS.md ./

# -warnaserror here as well as on the host: a warning that only one of the two
# rejects is a warning nobody sees.
RUN dotnet build --no-restore -warnaserror

# Arguments append, so `docker compose run --rm tests --filter ...` works and the
# no-argument form runs everything.
ENTRYPOINT ["dotnet", "test", "--no-build", "--nologo"]
