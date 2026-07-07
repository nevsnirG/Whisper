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

            // The same transaction can read its own uncommitted write
            var uncommitted = await store.ReadNextBatch(10, DateTimeOffset.UtcNow, CancellationToken.None);
            uncommitted.Should().ContainSingle()
                .Which.Id.Should().Be(record.Id);
        }

        await transaction.RollbackAsync();
        await connection.CloseAsync();

        (await CountRecords()).Should().Be(0);
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
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM [dbo].[outboxrecords]";
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private sealed class TestConnectionLeaseProvider(ConnectionLease lease) : IConnectionLeaseProvider
    {
        public ValueTask<ConnectionLease> Provide(CancellationToken cancellationToken)
            => ValueTask.FromResult(lease);
    }
}
