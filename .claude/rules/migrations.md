---
paths:
  - "**/Migrations/**"
---

# EF Core migrations

Each module project is its own startup project: it references `Microsoft.EntityFrameworkCore.Design`
and has an `IDesignTimeDbContextFactory`. The factory reads `ENCORE_<MODULE>_CONNECTION`
and falls back to the compose Postgres at `localhost:55432` (`docker compose up -d`).
Run the commands from the repo root.

## Adding a migration

| Module | Command |
|---|---|
| Catalog | `dotnet ef migrations add <Name> --project src/Encore.Modules.Catalog --startup-project src/Encore.Modules.Catalog --context CatalogDbContext --output-dir Data/Migrations` |
| Inventory | `dotnet ef migrations add <Name> --project src/Encore.Modules.Inventory --startup-project src/Encore.Modules.Inventory --context InventoryDbContext --output-dir Adapters/Persistence/Migrations` |
| Orders | `dotnet ef migrations add <Name> --project src/Encore.Modules.Orders --startup-project src/Encore.Modules.Orders --context OrdersDbContext --output-dir Data/Migrations` |
| Payments | `dotnet ef migrations add <Name> --project src/Encore.Modules.Payments --startup-project src/Encore.Modules.Payments --context PaymentsDbContext --output-dir Data/Migrations` |
| Notifications | `dotnet ef migrations add <Name> --project src/Encore.Modules.Notifications --startup-project src/Encore.Modules.Notifications --context NotificationsDbContext --output-dir Data/Migrations` |

- To apply migrations, replace `migrations add <Name>` with `database update` and drop `--output-dir`.
  This explicit command is the real path. `{Module}:MigrateOnStartup` is set only by the
  run profiles (013).
- To list migrations without a database, use `migrations list --no-connect` with the same
  `--project`, `--startup-project` and `--context`.
- Use the `dotnet ef` commands to create and remove migrations, not hand-written files or
  edits to `*ModelSnapshot.cs`.

## What MigrationConventionTests checks

It scans every `*.cs` under a `Migrations/` folder in `src/`, skipping `*.Designer.cs` and
`*ModelSnapshot.cs`.

- `NoMigrationShouldCreateTheXminSystemColumn`: no line may contain `xmin = table.Column`.
  `xmin` is a Postgres system column (004). If a regenerated migration adds it, delete that
  column from `CreateTable` and keep the concurrency token mapped in the EF configuration.
- `TheExpiringHoldsIndexShouldBeFilteredToTheHeldStatus`: exactly one migration file is named
  `*AddExpiringHoldsIndex*`. It contains `ix_seats_expiring_holds` and the filter
  `"Status" = <n>`, where `<n>` is `SeatStatus.Held`'s value in `SeatStatus.cs` (006). Keep the
  explicit value on `SeatStatus.Held` and give a new member a new number. Renumbering `Held`
  fails this test against the existing migration.
- `ThereShouldBeMigrationsToInspect`: the scan finds at least one migration.

`InventoryLayeringTests` also counts types declared in Inventory's migrations as adapter types.
So `Application/` and `Ports/` must not name a migration class.
