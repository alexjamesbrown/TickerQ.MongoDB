using MongoDB.Driver;
using TickerQ.MongoDB.Infrastructure;
using TickerQ.Utilities;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Enums;
using TickerQ.Utilities.Models;

namespace TickerQ.MongoDB.Tests;

[Collection("Mongo")]
public class MongoPersistenceProviderTests : IClassFixture<MongoTestFixture>, IAsyncLifetime
{
    private readonly MongoTestFixture _f;

    public MongoPersistenceProviderTests(MongoTestFixture fixture) => _f = fixture;

    public Task InitializeAsync() => _f.DropAllAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static TimeTickerEntity TickerRef(Guid id, DateTime updatedAt)
    {
        var t = new TimeTickerEntity { Id = id };
        InternalSetters<TimeTickerEntity>.Set(t, nameof(TimeTickerEntity.UpdatedAt), updatedAt);
        return t;
    }

    private TimeTickerEntity NewTimeTicker(DateTime? executionTime = null, TickerStatus status = TickerStatus.Idle)
    {
        var t = new TimeTickerEntity
        {
            Id = Guid.NewGuid(),
            Function = "test-fn",
            Request = Array.Empty<byte>(),
            ExecutionTime = executionTime ?? _f.FixedNow.AddSeconds(1)
        };
        typeof(TimeTickerEntity).GetProperty(nameof(t.Status))!.SetValue(t, status);
        typeof(TimeTickerEntity).GetProperty(nameof(t.CreatedAt))!.SetValue(t, _f.FixedNow);
        typeof(TimeTickerEntity).GetProperty(nameof(t.UpdatedAt))!.SetValue(t, _f.FixedNow);
        return t;
    }

    private CronTickerEntity NewCron(string function = "cron-fn", string expression = "* * * * *")
        => new()
        {
            Id = Guid.NewGuid(),
            Function = function,
            Expression = expression,
            Request = Array.Empty<byte>(),
            IsEnabled = true
        };

    [Fact]
    public async Task InsertAndGetTimeTicker_RoundTrips()
    {
        var ticker = NewTimeTicker();
        await _f.Provider.AddTimeTickers([ticker], CancellationToken.None);

        var fetched = await _f.Provider.GetTimeTickerById(ticker.Id, CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(ticker.Function, fetched!.Function);
        Assert.Equal(TickerStatus.Idle, fetched.Status);
    }

    [Fact]
    public async Task GetEarliestTimeTickers_ReturnsIdleWithinOneSecond()
    {
        var ticker = NewTimeTicker(executionTime: _f.FixedNow.AddMilliseconds(500));
        await _f.Provider.AddTimeTickers([ticker], CancellationToken.None);

        var earliest = await _f.Provider.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(earliest);
        Assert.Equal(ticker.Id, earliest[0].Id);
    }

    [Fact]
    public async Task GetEarliestTimeTickers_SkipsStaleRows()
    {
        var stale = NewTimeTicker(executionTime: _f.FixedNow.AddSeconds(-5));
        await _f.Provider.AddTimeTickers([stale], CancellationToken.None);

        var earliest = await _f.Provider.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Empty(earliest);
    }

    [Fact]
    public async Task QueueTimeTickers_AcquiresLockOnce_RejectsStaleUpdatedAt()
    {
        var ticker = NewTimeTicker();
        await _f.Provider.AddTimeTickers([ticker], CancellationToken.None);

        var fresh = await _f.Provider.GetTimeTickerById(ticker.Id, CancellationToken.None);
        Assert.NotNull(fresh);

        var queued1 = new List<TimeTickerEntity>();
        await foreach (var t in _f.Provider.QueueTimeTickers([TickerRef(fresh!.Id, fresh.UpdatedAt)], CancellationToken.None))
            queued1.Add(t);
        Assert.Single(queued1);

        // Second attempt with the old UpdatedAt should win nothing — CAS fails.
        var queued2 = new List<TimeTickerEntity>();
        await foreach (var t in _f.Provider.QueueTimeTickers([TickerRef(fresh.Id, fresh.UpdatedAt)], CancellationToken.None))
            queued2.Add(t);
        Assert.Empty(queued2);
    }

    [Fact]
    public async Task ReleaseAcquiredTimeTickers_ClearsLockAndResetsStatus()
    {
        var ticker = NewTimeTicker();
        await _f.Provider.AddTimeTickers([ticker], CancellationToken.None);

        var fresh = await _f.Provider.GetTimeTickerById(ticker.Id, CancellationToken.None);
        await foreach (var _ in _f.Provider.QueueTimeTickers([TickerRef(fresh!.Id, fresh.UpdatedAt)], CancellationToken.None)) { }

        await _f.Provider.ReleaseAcquiredTimeTickers([ticker.Id], CancellationToken.None);

        var after = await _f.Provider.GetTimeTickerById(ticker.Id, CancellationToken.None);
        Assert.Equal(TickerStatus.Idle, after!.Status);
        Assert.Null(after.LockHolder);
        Assert.Null(after.LockedAt);
    }

    [Fact]
    public async Task UpdateTimeTicker_AppliesStatusAndElapsedTime()
    {
        var ticker = NewTimeTicker();
        await _f.Provider.AddTimeTickers([ticker], CancellationToken.None);

        var ctx = new InternalFunctionContext
        {
            TickerId = ticker.Id,
            FunctionName = "test-fn",
            Type = TickerType.TimeTicker,
            ExecutedAt = _f.FixedNow,
        }
        .SetProperty(c => c.Status, TickerStatus.Done)
        .SetProperty(c => c.ExecutedAt, _f.FixedNow)
        .SetProperty(c => c.ElapsedTime, 123L)
        .SetProperty(c => c.ReleaseLock, true);

        var modified = await _f.Provider.UpdateTimeTicker(ctx, CancellationToken.None);
        Assert.Equal(1, modified);

        var after = await _f.Provider.GetTimeTickerById(ticker.Id, CancellationToken.None);
        Assert.Equal(TickerStatus.Done, after!.Status);
        Assert.Equal(123L, after.ElapsedTime);
        Assert.Null(after.LockHolder);
    }

    [Fact]
    public async Task ReleaseDeadNodeTimeTickerResources_ClearsLocksForThatNode()
    {
        var ticker = NewTimeTicker();
        await _f.Provider.AddTimeTickers([ticker], CancellationToken.None);

        // Force-lock under another node id by direct write to simulate a dead node.
        await _f.TimeTickers.UpdateOneAsync(
            Builders<TimeTickerEntity>.Filter.Eq(x => x.Id, ticker.Id),
            Builders<TimeTickerEntity>.Update
                .Set(x => x.LockHolder, "dead-node")
                .Set(x => x.LockedAt, _f.FixedNow)
                .Set(x => x.Status, TickerStatus.Queued));

        await _f.Provider.ReleaseDeadNodeTimeTickerResources("dead-node", CancellationToken.None);

        var after = await _f.Provider.GetTimeTickerById(ticker.Id, CancellationToken.None);
        Assert.Equal(TickerStatus.Idle, after!.Status);
        Assert.Null(after.LockHolder);
    }

    [Fact]
    public async Task InsertCronTickerOccurrences_UniqueIndexBlocksDuplicateSlots()
    {
        var cron = NewCron();
        await _f.Provider.InsertCronTickers([cron], CancellationToken.None);

        var execTime = _f.FixedNow.AddSeconds(5);
        var occ1 = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(),
            CronTickerId = cron.Id,
            ExecutionTime = execTime,
            Status = TickerStatus.Queued,
            CreatedAt = _f.FixedNow,
            UpdatedAt = _f.FixedNow
        };
        var occ2 = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(),
            CronTickerId = cron.Id,
            ExecutionTime = execTime,
            Status = TickerStatus.Queued,
            CreatedAt = _f.FixedNow,
            UpdatedAt = _f.FixedNow
        };

        var inserted = await _f.Provider.InsertCronTickerOccurrences([occ1, occ2], CancellationToken.None);
        Assert.Equal(1, inserted);
    }

    [Fact]
    public async Task SkipStaleCronOccurrences_MarksOldRowsAsSkipped()
    {
        var cron = NewCron();
        await _f.Provider.InsertCronTickers([cron], CancellationToken.None);

        var occ = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(),
            CronTickerId = cron.Id,
            ExecutionTime = _f.FixedNow.AddSeconds(-60),
            Status = TickerStatus.Idle,
            CreatedAt = _f.FixedNow.AddSeconds(-120),
            UpdatedAt = _f.FixedNow.AddSeconds(-120)
        };
        await _f.Provider.InsertCronTickerOccurrences([occ], CancellationToken.None);

        var skipped = await _f.Provider.SkipStaleCronOccurrencesAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(1, skipped);

        var after = await _f.CronTickerOccurrences
            .Find(Builders<CronTickerOccurrenceEntity<CronTickerEntity>>.Filter.Eq(x => x.Id, occ.Id))
            .FirstOrDefaultAsync();
        Assert.Equal(TickerStatus.Skipped, after.Status);
    }

    [Fact]
    public async Task IndexProvisioner_IsIdempotent_OnRepeatedCalls()
    {
        // Fixture already ran StartAsync once. Run again — should not throw.
        var provisioner = _f.NewProvisioner();
        await provisioner.StartAsync(CancellationToken.None);
        await provisioner.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GetAllCronTickerExpressions_ExcludesDisabledAndPaused()
    {
        var enabled = NewCron("enabled-fn");
        var disabled = NewCron("disabled-fn");
        disabled.IsEnabled = false;
        var paused = NewCron("paused-fn");
        paused.IsSystemPaused = true;

        await _f.Provider.InsertCronTickers([enabled, disabled, paused], CancellationToken.None);

        var result = await _f.Provider.GetAllCronTickerExpressions(CancellationToken.None);
        Assert.Single(result);
        Assert.Equal("enabled-fn", result[0].Function);
    }

    // ====================================================================
    // Coverage for scheduler-critical paths
    // ====================================================================

    [Fact]
    public async Task QueueCronTickerOccurrences_InsertPath_CreatesNewRowWithCronSnapshot()
    {
        var cron = NewCron();
        await _f.Provider.InsertCronTickers([cron], CancellationToken.None);

        var execTime = _f.FixedNow.AddSeconds(2);
        var ctx = new InternalManagerContext(cron.Id)
        {
            FunctionName = cron.Function,
            Expression = cron.Expression,
            Retries = 0,
            RetryIntervals = Array.Empty<int>(),
            NextCronOccurrence = null // INSERT path
        };

        var queued = new List<CronTickerOccurrenceEntity<CronTickerEntity>>();
        await foreach (var occ in _f.Provider.QueueCronTickerOccurrences((execTime, new[] { ctx }), CancellationToken.None))
            queued.Add(occ);

        Assert.Single(queued);
        Assert.Equal(TickerStatus.Queued, queued[0].Status);
        Assert.Equal(MongoTestFixture.NodeId, queued[0].LockHolder);
        Assert.NotNull(queued[0].CronTicker);
        Assert.Equal(cron.Function, queued[0].CronTicker.Function);

        // Verify the row was actually persisted with the right (CronTickerId, ExecutionTime)
        var persisted = await _f.CronTickerOccurrences
            .Find(Builders<CronTickerOccurrenceEntity<CronTickerEntity>>.Filter.And(
                Builders<CronTickerOccurrenceEntity<CronTickerEntity>>.Filter.Eq(x => x.CronTickerId, cron.Id),
                Builders<CronTickerOccurrenceEntity<CronTickerEntity>>.Filter.Eq(x => x.ExecutionTime, execTime)))
            .FirstOrDefaultAsync();
        Assert.NotNull(persisted);
        Assert.Equal(TickerStatus.Queued, persisted.Status);
        Assert.Equal(MongoTestFixture.NodeId, persisted.LockHolder);
    }

    [Fact]
    public async Task QueueCronTickerOccurrences_UpdatePath_ClaimsExistingIdleRow()
    {
        var cron = NewCron();
        await _f.Provider.InsertCronTickers([cron], CancellationToken.None);

        // Seed an existing Idle occurrence that the scheduler will claim
        var execTime = _f.FixedNow.AddSeconds(2);
        var existing = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(),
            CronTickerId = cron.Id,
            ExecutionTime = execTime,
            Status = TickerStatus.Idle,
            CreatedAt = _f.FixedNow.AddSeconds(-10),
            UpdatedAt = _f.FixedNow.AddSeconds(-10)
        };
        await _f.Provider.InsertCronTickerOccurrences([existing], CancellationToken.None);

        var ctx = new InternalManagerContext(cron.Id)
        {
            FunctionName = cron.Function,
            Expression = cron.Expression,
            Retries = 0,
            RetryIntervals = Array.Empty<int>(),
            // NextCronOccurrence's primary-ctor `createdAt` parameter is captured but never
            // assigned to the property (upstream quirk in 10.4.0), so set it explicitly.
            NextCronOccurrence = new NextCronOccurrence(existing.Id, existing.CreatedAt) { CreatedAt = existing.CreatedAt }
        };

        var queued = new List<CronTickerOccurrenceEntity<CronTickerEntity>>();
        await foreach (var occ in _f.Provider.QueueCronTickerOccurrences((execTime, new[] { ctx }), CancellationToken.None))
            queued.Add(occ);

        Assert.Single(queued);
        Assert.Equal(existing.Id, queued[0].Id);
        Assert.Equal(existing.CreatedAt, queued[0].CreatedAt); // CreatedAt preserved
        Assert.Equal(TickerStatus.Queued, queued[0].Status);
        Assert.Equal(MongoTestFixture.NodeId, queued[0].LockHolder);

        // Verify the original row in Mongo was updated (not a new row inserted)
        var rows = await _f.CronTickerOccurrences
            .Find(Builders<CronTickerOccurrenceEntity<CronTickerEntity>>.Filter.Eq(x => x.CronTickerId, cron.Id))
            .ToListAsync();
        Assert.Single(rows);
        Assert.Equal(existing.Id, rows[0].Id);
        Assert.Equal(TickerStatus.Queued, rows[0].Status);
    }

    [Fact]
    public async Task QueueTimedOutTimeTickers_ReacquiresStaleQueuedRows()
    {
        // A row that was acquired by some node but never completed; fallback path
        // must pick it up and mark InProgress.
        var stale = NewTimeTicker(
            executionTime: _f.FixedNow.AddSeconds(-30),
            status: TickerStatus.Queued);
        await _f.Provider.AddTimeTickers([stale], CancellationToken.None);

        var yielded = new List<TimeTickerEntity>();
        await foreach (var t in _f.Provider.QueueTimedOutTimeTickers(CancellationToken.None))
            yielded.Add(t);

        Assert.Single(yielded);
        Assert.Equal(stale.Id, yielded[0].Id);

        // The row itself should now be InProgress + locked to our node
        var persisted = await _f.TimeTickers
            .Find(Builders<TimeTickerEntity>.Filter.Eq(x => x.Id, stale.Id))
            .FirstOrDefaultAsync();
        Assert.Equal(TickerStatus.InProgress, persisted.Status);
        Assert.Equal(MongoTestFixture.NodeId, persisted.LockHolder);
        Assert.NotNull(persisted.LockedAt);
    }

    [Fact]
    public async Task GetEarliestAvailableCronOccurrence_ReturnsNextEligibleWithCronTickerLoaded()
    {
        var cron = NewCron();
        await _f.Provider.InsertCronTickers([cron], CancellationToken.None);

        // Two occurrences within the main-scheduler window (>= now - 1s).
        // The earlier one should win.
        var earlierExec = _f.FixedNow.AddMilliseconds(200);
        var laterExec = _f.FixedNow.AddSeconds(5);
        var earlier = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(),
            CronTickerId = cron.Id,
            ExecutionTime = earlierExec,
            Status = TickerStatus.Idle,
            CreatedAt = _f.FixedNow,
            UpdatedAt = _f.FixedNow
        };
        var later = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(),
            CronTickerId = cron.Id,
            ExecutionTime = laterExec,
            Status = TickerStatus.Idle,
            CreatedAt = _f.FixedNow,
            UpdatedAt = _f.FixedNow
        };
        await _f.Provider.InsertCronTickerOccurrences([earlier, later], CancellationToken.None);

        var picked = await _f.Provider.GetEarliestAvailableCronOccurrence(
            new[] { cron.Id },
            CancellationToken.None);

        Assert.NotNull(picked);
        Assert.Equal(earlier.Id, picked!.Id);
        // CronTicker reference must be hydrated for the dispatcher
        Assert.NotNull(picked.CronTicker);
        Assert.Equal(cron.Function, picked.CronTicker.Function);
        Assert.Equal(cron.Expression, picked.CronTicker.Expression);
    }

    [Fact]
    public async Task MigrateDefinedCronTickers_InsertsNewSeed_AndUpdatesExpressionInPlace()
    {
        // MigrateDefinedCronTickers runs an orphan-cleanup phase that deletes any *seeded*
        // cron (non-empty InitIdentifier) whose Function is not currently registered in
        // TickerFunctionProvider.TickerFunctions. To keep our seed alive across the two
        // migration calls, register the function for the duration of the test.
        var savedFunctions = TickerFunctionProvider.TickerFunctions
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var withSeedFn = new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>(savedFunctions)
        {
            ["seed-fn"] = ("", TickerTaskPriority.Normal, (_, _, _) => Task.CompletedTask, 0)
        };
        TickerFunctionProvider.ReplaceFunctions(withSeedFn);

        try
        {
            // First migration: should seed a brand new cron ticker
            await _f.Provider.MigrateDefinedCronTickers(
                new[] { ("seed-fn", "* * * * *") },
                CancellationToken.None);

            var afterFirst = await _f.CronTickers
                .Find(Builders<CronTickerEntity>.Filter.Eq(x => x.Function, "seed-fn"))
                .ToListAsync();
            Assert.Single(afterFirst);
            Assert.Equal("* * * * *", afterFirst[0].Expression);
            Assert.False(string.IsNullOrEmpty(afterFirst[0].InitIdentifier),
                "Seeded crons must carry an InitIdentifier so orphan-cleanup can recognise them.");

            // Second migration with a different expression: should update in place, not duplicate
            await _f.Provider.MigrateDefinedCronTickers(
                new[] { ("seed-fn", "0 0 * * *") },
                CancellationToken.None);

            var afterSecond = await _f.CronTickers
                .Find(Builders<CronTickerEntity>.Filter.Eq(x => x.Function, "seed-fn"))
                .ToListAsync();
            Assert.Single(afterSecond);
            Assert.Equal(afterFirst[0].Id, afterSecond[0].Id);
            Assert.Equal("0 0 * * *", afterSecond[0].Expression);
        }
        finally
        {
            TickerFunctionProvider.ReplaceFunctions(savedFunctions);
        }
    }

    [Fact]
    public async Task GetTimeTickerById_PopulatesChildrenHierarchy()
    {
        var root = NewTimeTicker();
        var child1 = NewTimeTicker();
        var child2 = NewTimeTicker();
        var grandchild = NewTimeTicker();

        child1.Children.Add(grandchild);
        root.Children.Add(child1);
        root.Children.Add(child2);

        await _f.Provider.AddTimeTickers([root], CancellationToken.None);

        var fetched = await _f.Provider.GetTimeTickerById(root.Id, CancellationToken.None);
        Assert.NotNull(fetched);

        var children = fetched!.Children.ToList();
        Assert.Equal(2, children.Count);

        var fetchedChild1 = children.SingleOrDefault(c => c.Id == child1.Id);
        var fetchedChild2 = children.SingleOrDefault(c => c.Id == child2.Id);
        Assert.NotNull(fetchedChild1);
        Assert.NotNull(fetchedChild2);

        Assert.Single(fetchedChild1!.Children);
        Assert.Equal(grandchild.Id, fetchedChild1.Children.Single().Id);
        Assert.Empty(fetchedChild2!.Children);
    }
}
