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
        var batch = await store.ReadNextBatch(10, CancellationToken.None);

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

        var batch = await store.ReadNextBatch(10, CancellationToken.None);
        batch.Should().BeEmpty();
        (await ReadDispatchedAtUtc(record.Id)).Should().Be(dispatchedAt);
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
        var batch = await store.ReadNextBatch(10, CancellationToken.None);

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
    public async Task SetFailedAt_FailedRecord_IsExcludedFromNextBatch()
    {
        var record = NewRecord();
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);

        await store.SetFailedAt(record, DateTimeOffset.UtcNow, CancellationToken.None);

        var batch = await store.ReadNextBatch(10, CancellationToken.None);
        batch.Should().BeEmpty();
    }

    [Fact]
    public async Task IncrementRetries_CalledTwice_NextBatchReturnsRecordWithTwoRetries()
    {
        var record = NewRecord();
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);

        await store.IncrementRetries(record, CancellationToken.None);
        await store.IncrementRetries(record, CancellationToken.None);

        var batch = await store.ReadNextBatch(10, CancellationToken.None);
        batch.Should().ContainSingle()
            .Which.Retries.Should().Be(2);
    }

    private static OutboxRecord NewRecord()
        => NewRecord(DateTimeOffset.UtcNow, typeof(TestDomainEvent), """{"Value":"test"}""");

    private static OutboxRecord NewRecord(DateTimeOffset enqueuedAtUtc, Type type, string payload) => new()
    {
        Id = Guid.NewGuid(),
        EnqueuedAtUtc = enqueuedAtUtc,
        AssemblyQualifiedType = type.AssemblyQualifiedName!,
        Payload = payload,
    };

    private async Task<DateTimeOffset?> ReadDispatchedAtUtc(Guid id)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DispatchedAtUtc FROM [dbo].[outboxrecords] WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (DateTimeOffset)value;
    }
}
