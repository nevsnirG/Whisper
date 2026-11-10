using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.MongoDb;

namespace Whisper.Outbox.IntegrationTests.MongoDb;

[Collection(MongoDbCollection.Name)]
public sealed class MongoDbOutboxInstallerTests(MongoDbFixture mongoFixture)
{
    private const string CollectionName = "outboxrecords";

    [Fact]
    public async Task InstallCollection_FreshDatabase_CreatesIndexes_AndSecondRunSucceeds()
    {
        var databaseName = NewDatabaseName();
        await using var provider = BuildServiceProvider(databaseName);
        var installer = provider.GetRequiredService<IInstallOutbox>();

        await installer.InstallCollection(CancellationToken.None);
        var act = () => installer.InstallCollection(CancellationToken.None);

        await act.Should().NotThrowAsync();
        var indexNames = await ListIndexNames(databaseName);
        indexNames.Should().Contain("ix_outbox_ready_by_due");
        indexNames.Should().Contain("ix_outbox_failed_by_failedat_id");
    }

    [Fact]
    public async Task InstallCollection_WithLegacyIndex_DropsItAndCreatesNewIndexes()
    {
        var databaseName = NewDatabaseName();
        await CreateLegacyIndex(databaseName);
        await using var provider = BuildServiceProvider(databaseName);
        var installer = provider.GetRequiredService<IInstallOutbox>();

        await installer.InstallCollection(CancellationToken.None);

        var indexNames = await ListIndexNames(databaseName);
        indexNames.Should().NotContain("ix_outbox_undispatched_by_id");
        indexNames.Should().Contain("ix_outbox_ready_by_due");
        indexNames.Should().Contain("ix_outbox_failed_by_failedat_id");
    }

    // Failed records are listed newest first, then by id. An index on the failure time alone cannot return them in
    // that order, so MongoDB sorted every failed record in memory for each page of the list.
    [Fact]
    public async Task InstallCollection_FailedIndex_FollowsTheOrderFailedRecordsAreListedIn()
    {
        var databaseName = NewDatabaseName();
        await using var provider = BuildServiceProvider(databaseName);

        await provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);

        var index = (await ListIndexes(databaseName)).Single(i => i["name"] == "ix_outbox_failed_by_failedat_id");
        index["key"].Should().Be(new BsonDocument { { "FailedAtUtc", -1 }, { "_id", 1 } });
    }

    [Fact]
    public async Task InstallCollection_WithTheFailedIndexOfAnEarlierVersion_ReplacesIt()
    {
        var databaseName = NewDatabaseName();
        await GetRawCollection(databaseName).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("FailedAtUtc", -1),
            new CreateIndexOptions { Name = "ix_outbox_failed_by_failedat" }));
        await using var provider = BuildServiceProvider(databaseName);

        await provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);

        var indexNames = await ListIndexNames(databaseName);
        indexNames.Should().NotContain("ix_outbox_failed_by_failedat");
        indexNames.Should().Contain("ix_outbox_failed_by_failedat_id");
    }

    // The index gets an entry for every record, so it is only worth having when something deletes dispatched ones.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallCollection_CreatesTheDispatchedIndex_OnlyWhenRetentionIsConfigured(bool retentionConfigured)
    {
        var databaseName = NewDatabaseName();
        await using var provider = BuildServiceProvider(databaseName, retentionConfigured ? TimeSpan.FromDays(7) : null);

        await provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);

        (await ListIndexNames(databaseName)).Contains("ix_outbox_dispatched_by_dispatchedat").Should().Be(retentionConfigured);
    }

    private static string NewDatabaseName() => $"whisper_test_{Guid.NewGuid():N}";

    private async Task<List<BsonDocument>> ListIndexes(string databaseName)
        => await (await GetRawCollection(databaseName).Indexes.ListAsync()).ToListAsync();

    private ServiceProvider BuildServiceProvider(string databaseName, TimeSpan? retainDispatchedFor)
    {
        var services = new ServiceCollection();
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Whispers).Assembly));
        services.AddWhisper(b =>
        {
            b.AddMediatR();
            b.AddOutbox(o => o
                .ConfigureWorker(w => w.RetainDispatchedFor = retainDispatchedFor)
                .AddMongoDb(new MongoDbOutboxConfiguration
                {
                    ConnectionString = mongoFixture.ConnectionString,
                    DatabaseName = databaseName,
                }));
        });
        return services.BuildServiceProvider();
    }

    private ServiceProvider BuildServiceProvider(string databaseName)
    {
        var services = new ServiceCollection();
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(Whispers).Assembly);
        });
        services.AddWhisper(b =>
        {
            b.AddMediatR();
            b.AddOutbox(o => o.AddMongoDb(new MongoDbOutboxConfiguration
            {
                ConnectionString = mongoFixture.ConnectionString,
                DatabaseName = databaseName,
            }));
        });
        return services.BuildServiceProvider();
    }

    private async Task CreateLegacyIndex(string databaseName)
    {
        var collection = GetRawCollection(databaseName);
        var keys = new BsonDocument { { "DispatchedAtUtc", 1 }, { "FailedAtUtc", 1 }, { "_id", 1 } };
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            keys,
            new CreateIndexOptions { Name = "ix_outbox_undispatched_by_id" }));
    }

    private async Task<string[]> ListIndexNames(string databaseName)
    {
        var indexes = await (await GetRawCollection(databaseName).Indexes.ListAsync()).ToListAsync();
        return [.. indexes.Select(i => i["name"].AsString)];
    }

    private IMongoCollection<BsonDocument> GetRawCollection(string databaseName)
        => new MongoClient(mongoFixture.ConnectionString)
            .GetDatabase(databaseName)
            .GetCollection<BsonDocument>(CollectionName);
}
