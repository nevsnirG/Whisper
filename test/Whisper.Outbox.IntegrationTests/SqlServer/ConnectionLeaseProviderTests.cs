using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.IntegrationTests.Fakes;
using Whisper.Outbox.SqlServer;

namespace Whisper.Outbox.IntegrationTests.SqlServer;

[Collection(MsSqlCollection.Name)]
public sealed class ConnectionLeaseProviderTests : IAsyncLifetime
{
    private readonly MsSqlFixture _sqlFixture;
    private string _connectionString = null!;

    public ConnectionLeaseProviderTests(MsSqlFixture sqlFixture)
    {
        _sqlFixture = sqlFixture;
    }

    public async Task InitializeAsync()
    {
        _connectionString = await _sqlFixture.CreateDatabaseAsync();

        await using var provider = BuildServiceProvider(o => { });
        await provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Add_LeasedTransactionRolledBack_DoesNotPersistRecord()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var leaseProvider = new TestConnectionLeaseProvider(new ConnectionLease(connection, transaction));
        await using var provider = BuildServiceProvider(o =>
            o.UseConnectionLeaseProvider<TestConnectionLeaseProvider>(_ => leaseProvider));
        var record = new OutboxRecord
        {
            Id = Guid.NewGuid(),
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            AssemblyQualifiedType = typeof(TestDomainEvent).AssemblyQualifiedName!,
            Payload = """{"Value":"test"}""",
        };

        using (var scope = provider.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            await store.Add(record, CancellationToken.None);
        }

        // The write went through the lease: the leased transaction sees its own uncommitted record.
        var writtenInTheLeasedTransaction = await CountRecords(connection, transaction);
        await transaction.RollbackAsync();
        await connection.CloseAsync();

        writtenInTheLeasedTransaction.Should().Be(1);
        (await CountRecords()).Should().Be(0);
    }

    // A provider built on things that only exist during a request or a message cannot even be constructed in the
    // worker's scope. The worker must still get its store, because nothing it does needs the provider.
    [Fact]
    public async Task WorkerOperations_WorkWithAProviderThatCannotBeConstructedInTheirScope()
    {
        var record = new OutboxRecord
        {
            Id = Guid.NewGuid(),
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            AssemblyQualifiedType = typeof(TestDomainEvent).AssemblyQualifiedName!,
            Payload = """{"Value":"test"}""",
        };
        await using (var withoutProvider = BuildServiceProvider(_ => { }))
        using (var scope = withoutProvider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IOutboxStore>().Add(record, CancellationToken.None);
        await using var provider = BuildServiceProvider(o => o.UseConnectionLeaseProvider<UnconstructibleConnectionLeaseProvider>());
        using var workerScope = provider.CreateScope();

        var store = workerScope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var batch = await store.ReadNextBatch(10, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.SetDispatchedAt(record, DateTimeOffset.UtcNow, CancellationToken.None);

        batch.Should().ContainSingle().Which.Id.Should().Be(record.Id);
        (await store.ReadNextBatch(10, DateTimeOffset.UtcNow, CancellationToken.None)).Should().BeEmpty();
    }

    // The worker runs outside any unit of work, so nothing it does goes through the provider. This one fails
    // whenever it is asked, the way a provider does that reads a unit of work which is not there.
    [Fact]
    public async Task WorkerOperations_NeverAskTheProvider()
    {
        var record = new OutboxRecord
        {
            Id = Guid.NewGuid(),
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            AssemblyQualifiedType = typeof(TestDomainEvent).AssemblyQualifiedName!,
            Payload = """{"Value":"test"}""",
        };
        await using (var withoutProvider = BuildServiceProvider(_ => { }))
        using (var scope = withoutProvider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IOutboxStore>().Add(record, CancellationToken.None);
        await using var provider = BuildServiceProvider(o => o.UseConnectionLeaseProvider<ThrowingConnectionLeaseProvider>());
        using var workerScope = provider.CreateScope();
        var store = workerScope.ServiceProvider.GetRequiredService<IOutboxStore>();

        var batch = await store.ReadNextBatch(10, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.ScheduleRetry(record, new OutboxFailure("transient", DateTimeOffset.UtcNow), null, CancellationToken.None);
        await store.SetFailedAt(record, new OutboxFailure("broken", DateTimeOffset.UtcNow), CancellationToken.None);
        await store.SetDispatchedAt(record, DateTimeOffset.UtcNow.AddDays(-30), CancellationToken.None);
        var deleted = await store.DeleteDispatchedBefore(DateTimeOffset.UtcNow.AddDays(-7), 10, CancellationToken.None);
        var storing = () => store.Add(record, CancellationToken.None);

        batch.Should().ContainSingle().Which.Id.Should().Be(record.Id);
        deleted.Should().Be(1);
        await storing.Should().ThrowAsync<InvalidOperationException>("storing events is what the provider is for")
            .WithMessage(ThrowingConnectionLeaseProvider.Message);
    }

    // A provider has nothing to lease in a scope without a unit of work, such as a plain HTTP request next to
    // message handlers. The store then manages its own connection, as it does when no provider is registered.
    [Fact]
    public async Task ProviderWithoutALease_StoreReadsAndWritesOnItsOwnConnection()
    {
        var leaseProvider = new TestConnectionLeaseProvider(lease: null);
        await using var provider = BuildServiceProvider(o =>
            o.UseConnectionLeaseProvider<TestConnectionLeaseProvider>(_ => leaseProvider));
        var record = new OutboxRecord
        {
            Id = Guid.NewGuid(),
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            AssemblyQualifiedType = typeof(TestDomainEvent).AssemblyQualifiedName!,
            Payload = """{"Value":"test"}""",
        };

        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);
        var batch = await store.ReadNextBatch(10, DateTimeOffset.UtcNow, CancellationToken.None);

        batch.Should().ContainSingle().Which.Id.Should().Be(record.Id);
        (await CountRecords()).Should().Be(1, "the write committed on the store's own connection");
    }

    private ServiceProvider BuildServiceProvider(Action<IOutboxBuilder> configureOutbox)
    {
        var services = new ServiceCollection();
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(Whispers).Assembly);
        });
        services.AddWhisper(b =>
        {
            b.AddMediatR();
            b.AddOutbox(o =>
            {
                o.AddSqlServer(new SqlOutboxConfiguration
                {
                    ConnectionString = _connectionString,
                });
                configureOutbox(o);
            });
        });
        return services.BuildServiceProvider();
    }

    private async Task<int> CountRecords()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        return await CountRecords(connection, transaction: null);
    }

    private static async Task<int> CountRecords(SqlConnection connection, SqlTransaction? transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM [dbo].[outboxrecords]";
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private sealed class TestConnectionLeaseProvider(ConnectionLease? lease) : IConnectionLeaseProvider
    {
        public ValueTask<ConnectionLease?> Provide(CancellationToken cancellationToken)
            => ValueTask.FromResult(lease);
    }

    private sealed class UnconstructibleConnectionLeaseProvider : IConnectionLeaseProvider
    {
        public UnconstructibleConnectionLeaseProvider()
            => throw new InvalidOperationException("This provider needs a request, and there is none in this scope.");

        public ValueTask<ConnectionLease?> Provide(CancellationToken cancellationToken)
            => ValueTask.FromResult<ConnectionLease?>(null);
    }

    private sealed class ThrowingConnectionLeaseProvider : IConnectionLeaseProvider
    {
        public const string Message = "There is no unit of work in this scope.";

        public ValueTask<ConnectionLease?> Provide(CancellationToken cancellationToken)
            => throw new InvalidOperationException(Message);
    }
}
