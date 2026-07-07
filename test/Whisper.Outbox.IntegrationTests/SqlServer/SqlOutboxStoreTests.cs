using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.IntegrationTests.Fakes;
using Whisper.Outbox.SqlServer;

namespace Whisper.Outbox.IntegrationTests.SqlServer;

[Collection(MsSqlCollection.Name)]
public sealed class SqlOutboxStoreTests : IAsyncLifetime
{
    private readonly MsSqlFixture _sqlFixture;
    private string _connectionString = null!;
    private ServiceProvider _provider = null!;

    public SqlOutboxStoreTests(MsSqlFixture sqlFixture)
    {
        _sqlFixture = sqlFixture;
    }

    public async Task InitializeAsync()
    {
        _connectionString = await _sqlFixture.CreateDatabaseAsync();

        var services = new ServiceCollection();
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(Whispers).Assembly);
        });
        services.AddWhisper(b =>
        {
            b.AddMediatR();
            b.AddOutbox(o => o.AddSqlServer(new SqlOutboxConfiguration
            {
                ConnectionString = _connectionString,
            }));
        });
        _provider = services.BuildServiceProvider();

        await _provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Add_SingleRecord_RoundTripsThroughReadNextBatch()
    {
        var record = NewRecord();
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        await store.Add(record, CancellationToken.None);
        var batch = await store.ReadNextBatch(10, DateTimeOffset.UtcNow, CancellationToken.None);

        var stored = batch.Should().ContainSingle().Subject;
        stored.Id.Should().Be(record.Id);
        stored.EnqueuedAtUtc.Should().Be(record.EnqueuedAtUtc);
        stored.Retries.Should().Be(0);
        stored.AssemblyQualifiedType.Should().Be(record.AssemblyQualifiedType);
        stored.Payload.Should().Be(record.Payload);
    }

    [Fact]
    public async Task SetDispatchedAt_DispatchedRecord_IsExcludedFromNextBatch()
    {
        var record = NewRecord();
        var dispatchedAt = DateTimeOffset.UtcNow;
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);

        await store.SetDispatchedAt(record, dispatchedAt, CancellationToken.None);

        var batch = await store.ReadNextBatch(10, DateTimeOffset.UtcNow, CancellationToken.None);
        batch.Should().BeEmpty();
        (await ReadRow(record.Id)).DispatchedAtUtc.Should().Be(dispatchedAt);
    }

    // Exercises the prepared-statement parameter-reuse loop in the batch Add overload;
    // distinct per-record values catch a parameter-not-reassigned regression.
    [Fact]
    public async Task Add_MultipleRecords_EachRoundTripsWithItsOwnValues()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var records = new[]
        {
            NewRecord(baseTime, typeof(TestDomainEvent), """{"Value":"first"}"""),
            NewRecord(baseTime.AddMilliseconds(1), typeof(TestDomainEventHandler), """{"Value":"second"}"""),
            NewRecord(baseTime.AddMilliseconds(2), typeof(OutboxRecord), """{"Value":"third"}"""),
        };
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        await store.Add(records, CancellationToken.None);
        var batch = await store.ReadNextBatch(10, DateTimeOffset.UtcNow, CancellationToken.None);

        batch.Should().HaveCount(records.Length);
        foreach (var record in records)
        {
            var stored = batch.Should().ContainSingle(r => r.Id == record.Id).Subject;
            stored.EnqueuedAtUtc.Should().Be(record.EnqueuedAtUtc);
            stored.Retries.Should().Be(0);
            stored.AssemblyQualifiedType.Should().Be(record.AssemblyQualifiedType);
            stored.Payload.Should().Be(record.Payload);
        }
    }

    [Fact]
    public async Task SetFailedAt_PersistsFailureFields_AndExcludesRecordFromNextBatch()
    {
        var record = NewRecord();
        var failedAt = DateTimeOffset.UtcNow;
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);

        await store.SetFailedAt(record, new OutboxFailure("System.Exception: it broke", failedAt), CancellationToken.None);

        var batch = await store.ReadNextBatch(10, DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
        batch.Should().BeEmpty();
        var row = await ReadRow(record.Id);
        row.FailedAtUtc.Should().Be(failedAt);
        row.LastError.Should().Be("System.Exception: it broke");
        row.LastErrorAtUtc.Should().Be(failedAt);
    }

    [Fact]
    public async Task ScheduleRetry_PersistsRetryAndFailureFields()
    {
        var record = NewRecord();
        var failedAt = DateTimeOffset.UtcNow;
        var nextRetryAt = failedAt.AddMinutes(5);
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);

        await store.ScheduleRetry(record, new OutboxFailure("System.Exception: transient", failedAt), nextRetryAt, CancellationToken.None);

        var row = await ReadRow(record.Id);
        row.Retries.Should().Be(1);
        row.NextRetryAtUtc.Should().Be(nextRetryAt);
        row.LastError.Should().Be("System.Exception: transient");
        row.LastErrorAtUtc.Should().Be(failedAt);
        row.FailedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task ScheduleRetry_CalledTwice_AccumulatesRetries()
    {
        var record = NewRecord();
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);

        await store.ScheduleRetry(record, new OutboxFailure("first", DateTimeOffset.UtcNow), null, CancellationToken.None);
        await store.ScheduleRetry(record, new OutboxFailure("second", DateTimeOffset.UtcNow), null, CancellationToken.None);

        var batch = await store.ReadNextBatch(10, DateTimeOffset.UtcNow, CancellationToken.None);
        batch.Should().ContainSingle()
            .Which.Retries.Should().Be(2);
        (await ReadRow(record.Id)).LastError.Should().Be("second");
    }

    [Fact]
    public async Task ScheduleRetry_WithNullNextRetry_LeavesRecordEligibleNow_AndHotReadOmitsLastError()
    {
        var record = NewRecord();
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);

        await store.ScheduleRetry(record, new OutboxFailure("transient", DateTimeOffset.UtcNow), null, CancellationToken.None);

        var batch = await store.ReadNextBatch(10, DateTimeOffset.UtcNow, CancellationToken.None);
        var stored = batch.Should().ContainSingle().Subject;
        stored.Id.Should().Be(record.Id);
        // Contract: the hot read path never loads LastError, even though it is persisted.
        stored.LastError.Should().BeNull();
        (await ReadRow(record.Id)).LastError.Should().Be("transient");
    }

    [Fact]
    public async Task ReadNextBatch_ExcludesNotDue_IncludesDueAndNull_OrderedNullsFirstThenDueTimeThenId()
    {
        var cutoff = DateTimeOffset.UtcNow;
        // Ids differ only in the final byte, which SQL Server compares first - the numeric
        // suffix order below therefore matches the uniqueidentifier sort order.
        var nullSecond = NewRecord(id: Guid.Parse("00000000-0000-0000-0000-000000000004"));
        var nullFirst = NewRecord(id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var dueEarlier = NewRecord(id: Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var dueAtCutoff = NewRecord(id: Guid.Parse("00000000-0000-0000-0000-000000000003"));
        var notDue = NewRecord(id: Guid.Parse("00000000-0000-0000-0000-000000000005"));
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(new[] { nullSecond, nullFirst, dueEarlier, dueAtCutoff, notDue }, CancellationToken.None);
        var failure = new OutboxFailure("transient", cutoff.AddMinutes(-30));
        await store.ScheduleRetry(dueEarlier, failure, cutoff.AddMinutes(-10), CancellationToken.None);
        await store.ScheduleRetry(dueAtCutoff, failure, cutoff, CancellationToken.None);
        await store.ScheduleRetry(notDue, failure, cutoff.AddTicks(1), CancellationToken.None);

        var batch = await store.ReadNextBatch(10, cutoff, CancellationToken.None);

        batch.Select(r => r.Id).Should().Equal(nullFirst.Id, nullSecond.Id, dueEarlier.Id, dueAtCutoff.Id);
    }

    [Fact]
    public async Task ReadNextBatch_RespectsBatchSize()
    {
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(new[] { NewRecord(), NewRecord(), NewRecord() }, CancellationToken.None);

        var batch = await store.ReadNextBatch(2, DateTimeOffset.UtcNow, CancellationToken.None);

        batch.Should().HaveCount(2);
    }

    private static OutboxRecord NewRecord(Guid? id = null)
        => NewRecord(DateTimeOffset.UtcNow, typeof(TestDomainEvent), """{"Value":"test"}""", id);

    private static OutboxRecord NewRecord(DateTimeOffset enqueuedAtUtc, Type type, string payload, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        EnqueuedAtUtc = enqueuedAtUtc,
        AssemblyQualifiedType = type.AssemblyQualifiedName!,
        Payload = payload,
    };

    private async Task<OutboxRow> ReadRow(Guid id)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT DispatchedAtUtc, FailedAtUtc, Retries, NextRetryAtUtc, LastError, LastErrorAtUtc
FROM [dbo].[outboxrecords] WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"record {id} should exist");
        return new OutboxRow(
            await reader.IsDBNullAsync(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
            await reader.IsDBNullAsync(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetInt32(2),
            await reader.IsDBNullAsync(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            await reader.IsDBNullAsync(4) ? null : reader.GetString(4),
            await reader.IsDBNullAsync(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
    }

    private sealed record OutboxRow(
        DateTimeOffset? DispatchedAtUtc,
        DateTimeOffset? FailedAtUtc,
        int Retries,
        DateTimeOffset? NextRetryAtUtc,
        string? LastError,
        DateTimeOffset? LastErrorAtUtc);
}
