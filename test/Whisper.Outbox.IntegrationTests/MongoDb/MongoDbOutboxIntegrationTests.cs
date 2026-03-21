using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MediatR;
using MongoDB.Driver;
using Whisper;
using Whisper.Abstractions;
using Whisper.Outbox.IntegrationTests.Fakes;
using Whisper.Outbox.MongoDb;

namespace Whisper.Outbox.IntegrationTests.MongoDb;

[Collection(MongoDbCollection.Name)]
public sealed class MongoDbOutboxIntegrationTests : IAsyncLifetime
{
    private readonly MongoDbFixture _mongoFixture;
    private readonly TestDomainEventHandler _handler;
    private IHost _host = null!;

    public MongoDbOutboxIntegrationTests(MongoDbFixture mongoFixture)
    {
        _mongoFixture = mongoFixture;
        _handler = new TestDomainEventHandler();
    }

    public async Task InitializeAsync()
    {
        var databaseName = $"whisper_test_{Guid.NewGuid():N}";

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // Register MediatR core services (scan an assembly with no handlers to avoid duplicates)
                services.AddMediatR(cfg =>
                {
                    cfg.RegisterServicesFromAssembly(typeof(Whispers).Assembly);
                });

                // Register our singleton test handler explicitly
                services.AddSingleton<INotificationHandler<TestDomainEvent>>(_handler);

                services.AddWhisper(b =>
                {
                    b.AddMediatR();
                    b.AddOutbox(o =>
                    {
                        o.ConfigureWorker(w => w.PollingIntervalMs = 50);
                        o.AddMongoDb(new MongoDbOutboxConfiguration
                        {
                            ConnectionString = _mongoFixture.ConnectionString,
                            DatabaseName = databaseName,
                        });
                    });
                });
            })
            .Build();

        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task DomainEvent_StoredInOutbox_IsDispatchedToMediatorHandler()
    {
        // Arrange
        var expectedValue = $"test-{Guid.NewGuid()}";
        var testEvent = new TestDomainEvent(expectedValue);

        // Act - Dispatch through the outbox (simulating what middleware does after collecting events)
        using (var scope = _host.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatchDomainEvents>();
            await dispatcher.Dispatch(testEvent, CancellationToken.None);
        }

        await _handler.WaitForFirstEvent(TimeSpan.FromSeconds(5));

        _handler.ReceivedEvents.Should().ContainSingle()
            .Which.Value.Should().Be(expectedValue);

        // Allow SetDispatchedAt to complete after the handler has fired
        var config = _host.Services.GetRequiredService<MongoDbOutboxConfiguration>();
        var mongoClient = _host.Services.GetRequiredService<MongoClient>();
        var collection = mongoClient
            .GetDatabase(config.DatabaseName)
            .GetCollection<OutboxRecord>(config.CollectionName);

        OutboxRecord? record = null;
        for (var i = 0; i < 10; i++)
        {
            var records = await collection.Find(FilterDefinition<OutboxRecord>.Empty).ToListAsync();
            record = records.SingleOrDefault();
            if (record?.DispatchedAtUtc is not null)
                break;
            await Task.Delay(50);
        }

        record.Should().NotBeNull();
        record!.DispatchedAtUtc.Should().NotBeNull();
    }
}
