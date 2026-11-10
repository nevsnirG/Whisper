using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.MongoDb;

namespace Whisper.Outbox.IntegrationTests.MongoDb;

[Collection(MongoDbCollection.Name)]
public sealed class MongoDbOutboxRetentionTests(MongoDbFixture mongoFixture)
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);

    // Nothing has expired: the state the cleanup finds on most of its runs. Without an index on the dispatch time
    // it reads every document in the collection to learn that.
    [Fact]
    public async Task DeleteDispatchedBefore_WithNothingExpired_FindsThatOutFromTheIndex()
    {
        var databaseName = $"whisper_test_{Guid.NewGuid():N}";
        await using var provider = BuildServiceProvider(databaseName);
        await provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);
        var collection = provider.GetRequiredService<MongoClient>().GetDatabase(databaseName).GetCollection<OutboxRecord>("outboxrecords");
        await collection.InsertManyAsync(Enumerable.Range(0, 500).Select(_ => new OutboxRecord
        {
            Id = Guid.NewGuid(),
            EnqueuedAtUtc = Now,
            DispatchedAtUtc = Now,
            AssemblyQualifiedType = "Some.Event, Some.Assembly",
            Payload = "{}",
        }));
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        var deleted = -1;
        var profiles = await MongoProfiler.Record(collection.Database, async () =>
            deleted = await store.DeleteDispatchedBefore(Now.AddDays(-7), 1000, CancellationToken.None));

        deleted.Should().Be(0);
        var search = profiles.FindCommand();
        search.Plan().Should().StartWith("IXSCAN { DispatchedAtUtc: 1 }");
        search.DocumentsRead().Should().Be(0, "the index alone shows that nothing is old enough");
    }

    private ServiceProvider BuildServiceProvider(string databaseName)
    {
        var services = new ServiceCollection();
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Whispers).Assembly));
        services.AddWhisper(b =>
        {
            b.AddMediatR();
            b.AddOutbox(o => o
                .ConfigureWorker(w => w.RetainDispatchedFor = TimeSpan.FromDays(7))
                .AddMongoDb(new MongoDbOutboxConfiguration
                {
                    ConnectionString = mongoFixture.ConnectionString,
                    DatabaseName = databaseName,
                }));
        });
        return services.BuildServiceProvider();
    }
}
