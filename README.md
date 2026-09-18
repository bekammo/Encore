# Encore

An event-ticketing platform, built as a modular monolith. A portfolio project
whose real subject is *where* architecture is worth paying for.

## The shape of it

Four modules behind one ASP.NET Core host. Three of them are deliberately
plain, and one is deliberately not:

| Module | Shape | Why |
|---|---|---|
| **Catalog** | Flat: `Endpoints` / `Data` / `Models` | Read-mostly CRUD. No contention, no invariants. |
| **Orders** | Flat: `Endpoints` / `Data` / `Models` | A record of what was bought. The hard parts live elsewhere. |
| **Payments** | Flat, plus `Simulation/` | A fake gateway that fails and hangs on demand, so the rest of the system has to cope with an unreliable dependency. |
| **Inventory** | Hexagonal: `Domain` / `Ports` / `Adapters` / `Application` | Seat contention under flash-sale load — the one genuinely hard problem. |

That asymmetry is the argument, not an accident. See
[DECISIONS.md](DECISIONS.md).

## Layout

```
Encore.sln
├── src/
│   ├── Encore.Api                        ASP.NET Core minimal API host
│   ├── Encore.Shared                     cross-cutting contracts, zero packages
│   ├── Encore.Modules.Catalog            flat CRUD
│   ├── Encore.Modules.Orders             flat CRUD
│   ├── Encore.Modules.Payments           flat CRUD + simulated gateway
│   ├── Encore.Modules.Inventory.Domain   the hexagon's interior — no packages
│   └── Encore.Modules.Inventory          ports, adapters, use cases
└── tests/
    ├── Encore.Modules.Inventory.UnitTests         domain + handler, in memory
    └── Encore.Modules.Inventory.IntegrationTests  adapters, via Testcontainers
```

`Encore.Modules.Inventory.Domain` has no `PackageReference` items at all, and
its csproj fails the build (`ENCORE001`) if one is added. EF Core, Redis and
ASP.NET Core exist only on the far side of the ports.

## Running it

```bash
docker compose up -d
dotnet build
dotnet run --project src/Encore.Api
```

`GET /health` is the only endpoint so far, which is correct — this is the
structure, not the implementation. Every module's registration seam
(`AddCatalogModule()`, `AddInventoryModule()`, …) already exists in
[Program.cs](src/Encore.Api/Program.cs) with an empty body, so that adding a
module's first service is a one-line change in one known place.

## Status

Scaffold only. Classes are stubs carrying a one-line description of the
responsibility they will take on; no state machine, no EF mappings, no Redis
calls yet.
