using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MediatR;
using MongoDB.Driver;
using Whisper.Abstractions;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.IntegrationTests.Fakes;
using Whisper.Outbox.MongoDb;

namespace Whisper.Outbox.IntegrationTests.MongoDb;

[Collection(MongoDbCollection.Name)]
public sealed class MongoDbRecoverabilityEndToEndTests : IAsyncLifetime
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly MongoDbFixture _mongoFixture;
    private readonly ToggleFailingDomainEventHandler _handler = new();
    private IHost _host = null!;
    private IMongoCollection<OutboxRecord> _collection = null!;

    public MongoDbRecoverabilityEndToEndTests(MongoDbFixture mongoFixture)
    {
        _mongoFixture = mongoFixture;
    }

    public async Task InitializeAsync()
    {
        var configuration = new MongoDbOutboxConfiguration
        {
            ConnectionString = _mongoFixture.ConnectionString,
            DatabaseName = $"whisper_test_{Guid.NewGuid():N}",
        };

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddMediatR(cfg =>
                {
                    cfg.RegisterServicesFromAssembly(typeof(Whispers).Assembly);
                });
                services.AddSingleton<INotificationHandler<TestDomainEvent>>(_handler);

                services.AddWhisper(b =>
                {
                    b.AddMediatR();
                    b.AddOutbox(o =>
                    {
                        o.ConfigureWorker(w => w.PollingIntervalMs = 50);
                        // A single total attempt: the first failure permanently fails the record.
                        o.ConfigureRecoverability(r => r.MaxRetries = 1);
                        o.AddMongoDb(configuration);
                    });
                });
            })
            .Build();

        await _host.StartAsync();

        _collection = _host.Services.GetRequiredService<MongoClient>()
            .GetDatabase(configuration.DatabaseName)
            .GetCollection<OutboxRecord>(configuration.CollectionName);
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task FailedRecord_RetriedThroughManagementStore_IsRedispatched()
    {
        using (var scope = _host.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatchDomainEvents>();
            await dispatcher.Dispatch(new TestDomainEvent("e2e"), CancellationToken.None);
        }

        // The handler throws and MaxRetries is 1, so the worker permanently fails the record
        // and persists the exception.
        var failed = await WaitForRecord(r => r.FailedAtUtc is not null);
        failed.LastError.Should().Contain(ToggleFailingDomainEventHandler.FailureMessage);
        failed.LastErrorAtUtc.Should().NotBeNull();
        failed.DispatchedAtUtc.Should().BeNull();

        _handler.StopFailing();
        var managementStore = _host.Services.GetRequiredService<IOutboxManagementStore>();
        (await managementStore.Retry(failed.Id, CancellationToken.None)).Should().BeTrue();

        await _handler.WaitForFirstSuccess(Deadline);
        var dispatched = await WaitForRecord(r => r.DispatchedAtUtc is not null);
        dispatched.FailedAtUtc.Should().BeNull();
        dispatched.LastError.Should().NotBeNull("a successful dispatch keeps the error audit trail");
    }

    private async Task<OutboxRecord> WaitForRecord(Func<OutboxRecord, bool> condition)
    {
        OutboxRecord? last = null;
        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            var records = await _collection.Find(FilterDefinition<OutboxRecord>.Empty).ToListAsync();
            last = records.SingleOrDefault();
            if (last is not null && condition(last))
                return last;
            await Task.Delay(50);
        }

        throw new TimeoutException($"The outbox record did not reach the expected state within {Deadline}. Last observed record id: {last?.Id.ToString() ?? "<none>"}.");
    }
}
