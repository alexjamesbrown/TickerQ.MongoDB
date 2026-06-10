# TickerQ.MongoDB

[![CI](https://github.com/alexjamesbrown/TickerQ.MongoDB/actions/workflows/ci.yml/badge.svg)](https://github.com/alexjamesbrown/TickerQ.MongoDB/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/TickerQ.MongoDB.svg)](https://www.nuget.org/packages/TickerQ.MongoDB/)
[![Downloads](https://img.shields.io/nuget/dt/TickerQ.MongoDB.svg)](https://www.nuget.org/packages/TickerQ.MongoDB/)

MongoDB persistence provider for [TickerQ](https://tickerq.net/) - a drop-in alternative to `TickerQ.EntityFrameworkCore`.

Uses the official `MongoDB.Driver` directly. No EF Core, no ODM.

## Install

```bash
dotnet add package TickerQ.MongoDB
```

## Usage

```csharp
builder.Services.AddTickerQ(options =>
{
    options.AddOperationalStore(mongoOptions =>
    {
        mongoOptions.UseTickerQMongoClient(
            connectionString: "mongodb://localhost:27017",
            databaseName: "tickerq");
    });
});
```

Or reuse an `IMongoClient` you've already registered:

```csharp
services.AddSingleton<IMongoClient>(_ => new MongoClient("mongodb://localhost:27017"));

builder.Services.AddTickerQ(options =>
{
    options.AddOperationalStore(mongoOptions =>
    {
        mongoOptions.UseExistingMongoClient(databaseName: "tickerq");
    });
});
```

## Design notes

- Concurrency uses single-document atomic `findAndModify` with an `UpdatedAt` CAS guard. No MongoDB transactions needed — works on standalone Mongo, replica sets, and Atlas free tier.
- Indexes are created idempotently on startup, including a unique index on `(CronTickerId, ExecutionTime)` for cron-occurrence deduplication.
- Collection names default to `ticker_TimeTickers`, `ticker_CronTickers`, `ticker_CronTickerOccurrences`. Override with `mongoOptions.SetCollectionPrefix("…")`.

## Repository layout

```
src/TickerQ.MongoDB/            — the NuGet package
samples/TickerQ.Sample.MongoDB/ — minimal Web API demo
tests/TickerQ.MongoDB.Tests/    — Testcontainers-based integration tests
```

## Run the tests

Requires Docker. Tests use [Testcontainers.MongoDb](https://www.nuget.org/packages/Testcontainers.MongoDb) to spin up a real `mongo:7` container.

```bash
dotnet test
```

## Run the sample

```bash
docker run -d -p 27017:27017 mongo:7
dotnet run --project samples/TickerQ.Sample.MongoDB
curl -X POST http://localhost:55429/schedule-sample
```

## License

MIT OR Apache-2.0
