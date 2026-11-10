using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.IntegrationTests.Fakes;
using Whisper.Outbox.MongoDb;

namespace Whisper.Outbox.IntegrationTests.MongoDb;

/// <summary>
/// Several instances of one application share its outbox. Their workers must take turns: left alone, they all read
/// the same pending records and each instance dispatches every one of them.
/// </summary>
[Collection(MongoDbCollection.Name)]
public sealed class MongoDbMultiInstanceTests(MongoDbFixture mongoFixture)
{
    private const int Events = 60;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private readonly string _databaseName = $"whisper_test_{Guid.NewGuid():N}";

    [Fact]
    public async Task TwoInstancesStartingOnABacklog_DispatchEveryEventExactlyOnce()
    {
        var handler = new CountingDomainEventHandler();
        using var instanceA = BuildHost(handler);
        using var instanceB = BuildHost(handler);
        var collection = await StoreBacklog(instanceA.Services);

        await Task.WhenAll(instanceA.StartAsync(), instanceB.StartAsync());
        await WaitUntilNothingIsPending(collection);
        await Task.WhenAll(instanceA.StopAsync(), instanceB.StopAsync());

        handler.TimesHandled.Should().HaveCount(Events);
        handler.TimesHandled.Where(handled => handled.Value != 1).Should().BeEmpty("each event is dispatched by one instance only");
    }

    private IHost BuildHost(CountingDomainEventHandler handler)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Whispers).Assembly));
                services.AddSingleton<INotificationHandler<TestDomainEvent>>(handler);
                services.AddWhisper(b =>
                {
                    b.AddMediatR();
                    b.AddOutbox(o =>
                    {
                        o.ConfigureWorker(w => w.PollingIntervalMs = 20);
                        o.AddMongoDb(new MongoDbOutboxConfiguration
                        {
                            ConnectionString = mongoFixture.ConnectionString,
                            DatabaseName = _databaseName,
                        });
                    });
                });
            })
            .Build();
    }

    // Written before any worker runs, as after an outage: both instances find the whole backlog at their first poll.
    private async Task<IMongoCollection<OutboxRecord>> StoreBacklog(IServiceProvider services)
    {
        await services.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);

        var uuidProvider = services.GetRequiredService<IUuidProvider>();
        var records = Enumerable.Range(0, Events)
            .Select(number => new OutboxRecord
            {
                Id = uuidProvider.Provide(),
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                AssemblyQualifiedType = typeof(TestDomainEvent).AssemblyQualifiedName!,
                Payload = $$"""{"Value":"event-{{number}}"}""",
            })
            .ToArray();

        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IOutboxStore>().Add(records, CancellationToken.None);

        return services.GetRequiredService<MongoClient>().GetDatabase(_databaseName).GetCollection<OutboxRecord>("outboxrecords");
    }

    private static async Task WaitUntilNothingIsPending(IMongoCollection<OutboxRecord> collection)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            if (await collection.CountDocumentsAsync(record => record.DispatchedAtUtc == null) == 0)
                return;

            await Task.Delay(50);
        }

        throw new TimeoutException($"The outbox still held pending records after {Deadline}.");
    }
}
