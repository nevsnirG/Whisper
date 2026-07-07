using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.IntegrationTests.Fakes;
using Whisper.Outbox.MongoDb;

namespace Whisper.Outbox.IntegrationTests.MongoDb;

[Collection(MongoDbCollection.Name)]
public sealed class MongoDbOutboxManagementStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly MongoDbFixture _mongoFixture;
    private MongoDbOutboxConfiguration _configuration = null!;
    private ServiceProvider _provider = null!;
    private IOutboxManagementStore _managementStore = null!;
    private IMongoCollection<OutboxRecord> _typedCollection = null!;

    public MongoDbOutboxManagementStoreTests(MongoDbFixture mongoFixture)
    {
        _mongoFixture = mongoFixture;
    }

    public async Task InitializeAsync()
    {
        _configuration = new MongoDbOutboxConfiguration
        {
            ConnectionString = _mongoFixture.ConnectionString,
            DatabaseName = $"whisper_test_{Guid.NewGuid():N}",
        };
        _provider = BuildServiceProvider(_ => { });
        await _provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);
        _managementStore = _provider.GetRequiredService<IOutboxManagementStore>();
        _typedCollection = _provider.GetRequiredService<MongoClient>()
            .GetDatabase(_configuration.DatabaseName)
            .GetCollection<OutboxRecord>(_configuration.CollectionName);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task GetFailed_PagesFailedRecordsByFailedAtDescending_WithTotalCount()
    {
        var failed = new OutboxRecord[5];
        for (var i = 0; i < failed.Length; i++)
            failed[i] = await AddFailedRecord(BaseTime.AddMinutes(i), $"error-{i}");
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
        newest.FailedAtUtc.Should().Be(BaseTime.AddMinutes(4));
        newest.Retries.Should().Be(0);
        newest.AssemblyQualifiedType.Should().Be(failed[4].AssemblyQualifiedType);
        newest.LastError.Should().Be("error-4");
        newest.LastErrorAtUtc.Should().Be(BaseTime.AddMinutes(4));
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
        await _typedCollection.UpdateManyAsync(
            Builders<OutboxRecord>.Filter.Empty,
            Builders<OutboxRecord>.Update
                .Set(x => x.FailedAtUtc, (DateTimeOffset?)BaseTime)
                .Set(x => x.LastError, "bulk failure"));

        var minPage = await _managementStore.GetFailed(0, 0, CancellationToken.None);
        var maxPage = await _managementStore.GetFailed(1, 1000, CancellationToken.None);

        minPage.TotalCount.Should().Be(201);
        minPage.Records.Should().HaveCount(1, "page and pageSize below 1 are clamped to 1");
        maxPage.Records.Should().HaveCount(200, "pageSize is clamped to 200");
    }

    [Fact]
    public async Task Get_ExistingRecord_ReturnsFullRecordIncludingPayloadAndError()
    {
        var failedAt = BaseTime.AddMinutes(1);
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
        var record = await AddFailedRecord(BaseTime, "the reason", retries: 2);

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
        var failed1 = await AddFailedRecord(BaseTime, "a");
        var failed2 = await AddFailedRecord(BaseTime.AddMinutes(1), "b");
        var failed3 = await AddFailedRecord(BaseTime.AddMinutes(2), "c");
        var retrying = await AddRetryingRecord(BaseTime.AddMinutes(30));

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
        var record = await AddFailedRecord(BaseTime, "gone");

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

    // Host-middleware pitfall: a registered session provider (host unit of work) serves every
    // IOutboxStore operation, but the management store must never consult it - otherwise
    // dashboard operations would break or join the host's session.
    [Fact]
    public async Task AllOperations_WithThrowingMongoSessionProvider_StillSucceed()
    {
        var record = await AddFailedRecord(BaseTime, "pitfall");
        await using var pitfallProvider = BuildServiceProvider(o =>
            o.UseMongoSessionProvider(_ => new ThrowingMongoSessionProvider()));
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
                o.AddMongoDb(_configuration);
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
        await store.SetDispatchedAt(record, BaseTime, CancellationToken.None);
        return record;
    }

    private async Task<OutboxRecord> AddRetryingRecord(DateTimeOffset nextRetryAtUtc)
    {
        var record = NewRecord();
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);
        await store.ScheduleRetry(record, new OutboxFailure("transient", BaseTime), nextRetryAtUtc, CancellationToken.None);
        return record;
    }

    private static OutboxRecord NewRecord() => new()
    {
        Id = Guid.NewGuid(),
        EnqueuedAtUtc = BaseTime,
        AssemblyQualifiedType = typeof(TestDomainEvent).AssemblyQualifiedName!,
        Payload = """{"Value":"test"}""",
    };

    private sealed class ThrowingMongoSessionProvider : IMongoSessionProvider
    {
        public IClientSessionHandle? Session
            => throw new InvalidOperationException("The management store must never consult the session provider.");
    }
}
