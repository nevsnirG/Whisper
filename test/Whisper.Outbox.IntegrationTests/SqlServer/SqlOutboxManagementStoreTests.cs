using System.Transactions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.IntegrationTests.Fakes;
using Whisper.Outbox.SqlServer;

namespace Whisper.Outbox.IntegrationTests.SqlServer;

[Collection(MsSqlCollection.Name)]
public sealed class SqlOutboxManagementStoreTests : IAsyncLifetime
{
    private readonly MsSqlFixture _sqlFixture;
    private string _connectionString = null!;
    private ServiceProvider _provider = null!;
    private IOutboxManagementStore _managementStore = null!;

    public SqlOutboxManagementStoreTests(MsSqlFixture sqlFixture)
    {
        _sqlFixture = sqlFixture;
    }

    public async Task InitializeAsync()
    {
        _connectionString = await _sqlFixture.CreateDatabaseAsync();
        _provider = BuildServiceProvider(_ => { });
        await _provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);
        _managementStore = _provider.GetRequiredService<IOutboxManagementStore>();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task GetFailed_PagesFailedRecordsByFailedAtDescending_WithTotalCount()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var failed = new OutboxRecord[5];
        for (var i = 0; i < failed.Length; i++)
            failed[i] = await AddFailedRecord(baseTime.AddMinutes(i), $"error-{i}");
        await AddPendingRecord();
        await AddDispatchedRecord();

        var page1 = await _managementStore.GetFailed(1, 2, CancellationToken.None);
        var page2 = await _managementStore.GetFailed(2, 2, CancellationToken.None);
        var page3 = await _managementStore.GetFailed(3, 2, CancellationToken.None);

        page1.TotalCount.Should().Be(5);
        page1.Records.Select(r => r.Id).Should().Equal(failed[4].Id, failed[3].Id);
        page2.Records.Select(r => r.Id).Should().Equal(failed[2].Id, failed[1].Id);
        page3.Records.Select(r => r.Id).Should().Equal(failed[0].Id);

        var newest = page1.Records[0];
        newest.EnqueuedAtUtc.Should().Be(failed[4].EnqueuedAtUtc);
        newest.FailedAtUtc.Should().Be(baseTime.AddMinutes(4));
        newest.Retries.Should().Be(0);
        newest.AssemblyQualifiedType.Should().Be(failed[4].AssemblyQualifiedType);
        newest.LastError.Should().Be("error-4");
        newest.LastErrorAtUtc.Should().Be(baseTime.AddMinutes(4));
    }

    [Fact]
    public async Task GetFailed_ClampsPageAndPageSize()
    {
        var records = Enumerable.Range(0, 201).Select(_ => NewRecord()).ToArray();
        using (var scope = _provider.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            await store.Add(records, CancellationToken.None);
        }
        await FailAllRecords();

        var minPage = await _managementStore.GetFailed(0, 0, CancellationToken.None);
        var maxPage = await _managementStore.GetFailed(1, 1000, CancellationToken.None);

        minPage.TotalCount.Should().Be(201);
        minPage.Records.Should().HaveCount(1, "page and pageSize below 1 are clamped to 1");
        maxPage.Records.Should().HaveCount(200, "pageSize is clamped to 200");
    }

    [Fact]
    public async Task Get_ExistingRecord_ReturnsFullRecordIncludingPayloadAndError()
    {
        var failedAt = DateTimeOffset.UtcNow;
        var record = await AddFailedRecord(failedAt, "the reason");

        var stored = await _managementStore.Get(record.Id, CancellationToken.None);

        stored.Should().NotBeNull();
        stored!.Id.Should().Be(record.Id);
        stored.EnqueuedAtUtc.Should().Be(record.EnqueuedAtUtc);
        stored.Payload.Should().Be(record.Payload);
        stored.AssemblyQualifiedType.Should().Be(record.AssemblyQualifiedType);
        stored.FailedAtUtc.Should().Be(failedAt);
        stored.LastError.Should().Be("the reason");
        stored.LastErrorAtUtc.Should().Be(failedAt);
    }

    [Fact]
    public async Task Get_MissingRecord_ReturnsNull()
    {
        var stored = await _managementStore.Get(Guid.NewGuid(), CancellationToken.None);

        stored.Should().BeNull();
    }

    [Fact]
    public async Task Retry_FailedRecord_ResetsWorkerFieldsAndKeepsErrorAuditTrail()
    {
        var record = await AddFailedRecord(DateTimeOffset.UtcNow, "the reason", retries: 2);

        var result = await _managementStore.Retry(record.Id, CancellationToken.None);

        result.Should().BeTrue();
        var stored = await _managementStore.Get(record.Id, CancellationToken.None);
        stored!.FailedAtUtc.Should().BeNull();
        stored.Retries.Should().Be(0);
        stored.NextRetryAtUtc.Should().BeNull();
        stored.LastError.Should().Be("the reason", "the error is kept as an audit trail");
        stored.LastErrorAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Retry_PendingRecord_ReturnsFalseAndChangesNothing()
    {
        var record = await AddPendingRecord();

        var result = await _managementStore.Retry(record.Id, CancellationToken.None);

        result.Should().BeFalse();
        var stored = await _managementStore.Get(record.Id, CancellationToken.None);
        stored!.DispatchedAtUtc.Should().BeNull();
        stored.FailedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Retry_MissingRecord_ReturnsFalse()
    {
        var result = await _managementStore.Retry(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RetryAll_ResetsOnlyFailedRecords_AndReturnsCount()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var failed1 = await AddFailedRecord(baseTime, "a");
        var failed2 = await AddFailedRecord(baseTime.AddMinutes(1), "b");
        var failed3 = await AddFailedRecord(baseTime.AddMinutes(2), "c");
        var retrying = await AddRetryingRecord(baseTime.AddMinutes(30));

        var count = await _managementStore.RetryAll(CancellationToken.None);

        count.Should().Be(3);
        (await _managementStore.GetFailed(1, 10, CancellationToken.None)).TotalCount.Should().Be(0);
        foreach (var id in new[] { failed1.Id, failed2.Id, failed3.Id })
        {
            var stored = await _managementStore.Get(id, CancellationToken.None);
            stored!.FailedAtUtc.Should().BeNull();
            stored.Retries.Should().Be(0);
        }
        // The pending record scheduled for a later retry belongs to the worker and must not be touched.
        var untouched = await _managementStore.Get(retrying.Id, CancellationToken.None);
        untouched!.Retries.Should().Be(1);
        untouched.NextRetryAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_FailedRecord_ReturnsTrueAndRemovesRecord()
    {
        var record = await AddFailedRecord(DateTimeOffset.UtcNow, "gone");

        var result = await _managementStore.Delete(record.Id, CancellationToken.None);

        result.Should().BeTrue();
        (await _managementStore.Get(record.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_PendingRecord_ReturnsFalseAndKeepsRecord()
    {
        var record = await AddPendingRecord();

        var result = await _managementStore.Delete(record.Id, CancellationToken.None);

        result.Should().BeFalse();
        (await _managementStore.Get(record.Id, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_MissingRecord_ReturnsFalse()
    {
        var result = await _managementStore.Delete(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeFalse();
    }

    // Host-middleware pitfall: a registered connection lease provider (host unit of work)
    // serves every IOutboxStore operation, but the management store must never consult it -
    // otherwise dashboard operations would break or, worse, enlist in the host's transaction.
    [Fact]
    public async Task AllOperations_WithThrowingConnectionLeaseProvider_StillSucceed()
    {
        var record = await AddFailedRecord(DateTimeOffset.UtcNow, "pitfall");
        await using var pitfallProvider = BuildServiceProvider(o =>
            o.UseConnectionLeaseProvider<ThrowingConnectionLeaseProvider>(_ => new ThrowingConnectionLeaseProvider()));
        var managementStore = pitfallProvider.GetRequiredService<IOutboxManagementStore>();

        var page = await managementStore.GetFailed(1, 10, CancellationToken.None);
        var stored = await managementStore.Get(record.Id, CancellationToken.None);
        var retried = await managementStore.Retry(record.Id, CancellationToken.None);
        var retriedAll = await managementStore.RetryAll(CancellationToken.None);
        var deleted = await managementStore.Delete(record.Id, CancellationToken.None);

        page.TotalCount.Should().Be(1);
        stored.Should().NotBeNull();
        retried.Should().BeTrue();
        retriedAll.Should().Be(0, "the only failed record was already retried");
        deleted.Should().BeFalse("the record is no longer failed after the retry");
    }

    // Host-middleware pitfall: an ambient uncompleted TransactionScope from host unit-of-work
    // middleware must not swallow the retry. The management store connects with Enlist=false,
    // so its UPDATE commits even though the ambient scope is rolled back.
    [Fact]
    public async Task Retry_InsideAmbientUncompletedTransactionScope_StillCommits()
    {
        var record = await AddFailedRecord(DateTimeOffset.UtcNow, "ambient");

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            (await _managementStore.Retry(record.Id, CancellationToken.None)).Should().BeTrue();
            // Scope disposed without Complete(): the ambient transaction rolls back.
        }

        var stored = await _managementStore.Get(record.Id, CancellationToken.None);
        stored!.FailedAtUtc.Should().BeNull("the retry must have committed independently of the ambient transaction");
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

    private async Task<OutboxRecord> AddPendingRecord()
    {
        var record = NewRecord();
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);
        return record;
    }

    private async Task<OutboxRecord> AddFailedRecord(DateTimeOffset failedAtUtc, string error, int retries = 0)
    {
        var record = NewRecord();
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);
        for (var i = 0; i < retries; i++)
            await store.ScheduleRetry(record, new OutboxFailure(error, failedAtUtc), failedAtUtc.AddMinutes(5), CancellationToken.None);
        await store.SetFailedAt(record, new OutboxFailure(error, failedAtUtc), CancellationToken.None);
        return record;
    }

    private async Task<OutboxRecord> AddDispatchedRecord()
    {
        var record = NewRecord();
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);
        await store.SetDispatchedAt(record, DateTimeOffset.UtcNow, CancellationToken.None);
        return record;
    }

    private async Task<OutboxRecord> AddRetryingRecord(DateTimeOffset nextRetryAtUtc)
    {
        var record = NewRecord();
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);
        await store.ScheduleRetry(record, new OutboxFailure("transient", DateTimeOffset.UtcNow), nextRetryAtUtc, CancellationToken.None);
        return record;
    }

    private async Task FailAllRecords()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE [dbo].[outboxrecords]
SET FailedAtUtc = @failedAt, LastError = N'bulk failure', LastErrorAtUtc = @failedAt";
        command.Parameters.AddWithValue("@failedAt", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();
    }

    private static OutboxRecord NewRecord() => new()
    {
        Id = Guid.NewGuid(),
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
        AssemblyQualifiedType = typeof(TestDomainEvent).AssemblyQualifiedName!,
        Payload = """{"Value":"test"}""",
    };

    private sealed class ThrowingConnectionLeaseProvider : IConnectionLeaseProvider
    {
        public ValueTask<ConnectionLease> Provide(CancellationToken cancellationToken)
            => throw new InvalidOperationException("The management store must never consult the connection lease provider.");
    }
}
