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

        // Assert - OutboxWorker polls every 1s, allow up to 10s for processing
        await _handler.WaitForFirstEvent(TimeSpan.FromSeconds(10));

        _handler.ReceivedEvents.Should().ContainSingle()
            .Which.Value.Should().Be(expectedValue);

        // Assert the outbox record is marked as dispatched
        var config = _host.Services.GetRequiredService<MongoDbOutboxConfiguration>();
        var mongoClient = _host.Services.GetRequiredService<MongoClient>();
        var collection = mongoClient
            .GetDatabase(config.DatabaseName)
            .GetCollection<OutboxRecord>(config.CollectionName);

        var records = await collection.Find(FilterDefinition<OutboxRecord>.Empty).ToListAsync();
        records.Should().ContainSingle()
            .Which.DispatchedAtUtc.Should().NotBeNull();
    }
}
