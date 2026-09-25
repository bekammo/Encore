# syntax=docker/dockerfile:1

# Runs the test suite on Linux. Windows Smart App Control blocks freshly built, unsigned
# assemblies on the development machine with no exclusion mechanism; a container is not
# subject to it, and runs the suite the same way on any machine with Docker.
FROM mcr.microsoft.com/dotnet/sdk:10.0

WORKDIR /src

# Project files first, restored as their own layer, so source edits do not re-restore.
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props Encore.sln ./
COPY src/Encore.Api/Encore.Api.csproj                                          src/Encore.Api/
COPY src/Encore.Payments.Api/Encore.Payments.Api.csproj                        src/Encore.Payments.Api/
COPY src/Encore.Shared/Encore.Shared.csproj                                    src/Encore.Shared/
COPY src/Encore.Telemetry/Encore.Telemetry.csproj                              src/Encore.Telemetry/
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

# DECISIONS.md is a test input: the suite checks its index. Last, since it changes often.
COPY DECISIONS.md ./

# -warnaserror, matching the API image.
RUN dotnet build --no-restore -warnaserror

# Arguments append, so `docker compose run --rm tests --filter ...` works.
ENTRYPOINT ["dotnet", "test", "--no-build", "--nologo"]
