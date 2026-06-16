# TickerQ.MongoDB

[![CI](https://github.com/alexjamesbrown/TickerQ.MongoDB/actions/workflows/ci.yml/badge.svg)](https://github.com/alexjamesbrown/TickerQ.MongoDB/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/alexjamesbrown.TickerQ.MongoDB.svg)](https://www.nuget.org/packages/alexjamesbrown.TickerQ.MongoDB/)
[![Downloads](https://img.shields.io/nuget/dt/alexjamesbrown.TickerQ.MongoDB.svg)](https://www.nuget.org/packages/alexjamesbrown.TickerQ.MongoDB/)

MongoDB persistence provider for [TickerQ](https://tickerq.net/). Drop-in alternative to `TickerQ.EntityFrameworkCore` — pick one or the other, not both.

Uses the official `MongoDB.Driver` directly. No EF Core, no ODM.

> **Note on the package ID**: published as `alexjamesbrown.TickerQ.MongoDB` because Arcenox has reserved the `TickerQ.*` prefix on nuget.org. The third-party convention is `<Owner>.TickerQ.*` (see `eQuantic.TickerQ.*`, `Volo.Abp.TickerQ`, etc.). If/when an official `TickerQ.MongoDB` ships from Arcenox, this package will be deprecated in favour of it.

## Compatibility

| `alexjamesbrown.TickerQ.MongoDB` | `TickerQ.Utilities` | .NET    | `MongoDB.Driver` |
|----------------------------------|---------------------|---------|------------------|
| `0.1.x`                          | `[10.4.0, 11.0.0)`  | `net10` | `3.5.x`          |

Tested against `mongo:7`. Standalone, replica set, and Atlas (including the free tier) all work — concurrency uses single-document atomic operations, no multi-document transactions.

## Install

```bash
dotnet add package alexjamesbrown.TickerQ.MongoDB
```

## Quickstart

A minimal working Web API with a scheduled job, no `[TickerFunction]` attribute, no source generator:

```csharp
using TickerQ.DependencyInjection;
using TickerQ.MongoDB.DependencyInjection;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Interfaces.Managers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTickerQ(options =>
{
    options.AddOperationalStore(mongo =>
    {
        mongo.UseTickerQMongoClient(
            connectionString: "mongodb://localhost:27017",
            databaseName: "tickerq");
    });
});

// Lambda-based registration — no attribute needed
builder.Services.MapTicker("send-welcome-email", async (ctx, ct) =>
{
    Console.WriteLine($"firing {ctx.Id} at {DateTime.UtcNow:O}");
    await Task.Delay(100, ct);
});

var app = builder.Build();
app.UseTickerQ();

app.MapPost("/welcome", async (ITimeTickerManager<TimeTickerEntity> manager) =>
{
    var result = await manager.AddAsync(new TimeTickerEntity
    {
        Function = "send-welcome-email",
        ExecutionTime = DateTime.UtcNow.AddSeconds(5)
    });
    return Results.Ok(new { result.Result.Id });
});

app.Run();
```

Indexes are provisioned automatically on startup. No migrations to run.

## Configuration

### Reuse an existing `IMongoClient`

If you already have a `MongoClient` registered (e.g. for your application data), point TickerQ at it:

```csharp
services.AddSingleton<IMongoClient>(_ => new MongoClient("mongodb://localhost:27017"));

services.AddTickerQ(options =>
{
    options.AddOperationalStore(mongo =>
    {
        mongo.UseExistingMongoClient(databaseName: "tickerq");
    });
});
```

### Customise collection names

Defaults to `ticker_TimeTickers`, `ticker_CronTickers`, `ticker_CronTickerOccurrences`. Override with:

```csharp
mongo.SetCollectionPrefix("scheduler_"); // -> scheduler_TimeTickers, ...
```

Pass an empty string for no prefix.

## How it works

- **Concurrency**: every state transition is a single-document atomic `findAndModify` / `updateMany` guarded by an `UpdatedAt` CAS predicate — mirrors how the EF provider uses `ExecuteUpdateAsync(...).Where(x => x.UpdatedAt == old)`. Two schedulers racing on the same row, only one wins.
- **Cron occurrence dedup**: a unique index on `(CronTickerId, ExecutionTime)` enforces "at most one occurrence per slot." Duplicate-key errors from concurrent schedulers are treated as "lost the race" and skipped silently.
- **No transactions**: nothing in the hot path needs multi-document atomicity, so MongoDB transactions are never used. The provider works on standalone deployments without a replica set.
- **Indexes**: provisioned by an `IHostedService` on application start, idempotently. Includes the unique cron-occurrence index, status+executionTime compound indexes for the scheduler's queue scans, and a `ParentId` index for child-ticker lookups.

## Production notes

- **Index creation runs on first startup.** On an empty database it's a no-op; against a large existing collection (e.g. you switched providers), first start blocks until indexes are built. Provision them out-of-band first if that matters.
- **Replica set not required.** If you're on Atlas free tier or a single-node Mongo, you're fine.
- **Pin your `TickerQ.Utilities` version.** This package is built against `[10.4.0, 11.0.0)`. If a future major bump breaks ABI, we'll cut a matching major release here.
- **Time precision**: BSON stores DateTime at millisecond resolution. The scheduler operates at second resolution, so this is never observable.

## Note on the compat layer

`TickerQ.Utilities` 10.4.0 marks several entity setters and one options-builder property as `internal`, with `InternalsVisibleTo` only for `TickerQ.EntityFrameworkCore`. This package bridges the gap with `Expression.Compile()`-backed setter delegates that go through `PropertyInfo.SetMethod` (runtime reflection ignores C# accessibility checks, so it's safe and effectively free after the first call).

Once [Arcenox-co/TickerQ#868](https://github.com/Arcenox-co/TickerQ/pull/868) lands and a new `TickerQ.Utilities` ships, `Infrastructure/InternalSetters.cs`, `Infrastructure/EntityProjections.cs`, and the reflection block in `ServiceExtension.cs` will all be deleted in a single follow-up commit. The public API surface of this package will not change.

## Run the sample

Requires Docker for a local Mongo.

```bash
docker run -d -p 27017:27017 mongo:7
dotnet run --project samples/TickerQ.Sample.MongoDB
curl -X POST http://localhost:55429/schedule-sample
```

You should see the job fire ~5 seconds later, and the corresponding row in `tickerq.ticker_TimeTickers` transition `Idle` → `Queued` → `InProgress` → `Done`.

## Run the tests

Tests use [Testcontainers.MongoDb](https://www.nuget.org/packages/Testcontainers.MongoDb) to spin up a real `mongo:7` container per fixture — no mocks. Docker is required.

```bash
dotnet test
```

## Repository layout

```
src/TickerQ.MongoDB/            — the NuGet package
samples/TickerQ.Sample.MongoDB/ — minimal Web API demo
tests/TickerQ.MongoDB.Tests/    — Testcontainers-based integration tests
```

## License

MIT OR Apache-2.0
