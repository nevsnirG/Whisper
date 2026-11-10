using MongoDB.Bson;
using MongoDB.Driver;

namespace Whisper.Outbox.IntegrationTests.MongoDb;

/// <summary>
/// Records what MongoDB itself did for the commands a test runs, through the database profiler: which plan it
/// chose, how many documents it read, and whether it had to sort in memory.
/// </summary>
internal static class MongoProfiler
{
    /// <summary>Runs <paramref name="operation"/> with the profiler on and returns the profile of every command it sent.</summary>
    public static async Task<List<BsonDocument>> Record(IMongoDatabase database, Func<Task> operation)
    {
        await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 2));
        try
        {
            await operation();
        }
        finally
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 0));
        }

        return await database.GetCollection<BsonDocument>("system.profile")
            .Find(FilterDefinition<BsonDocument>.Empty)
            .ToListAsync();
    }

    /// <summary>The profile of the one aggregation among <paramref name="profiles"/> that returns a page (it has a $limit stage).</summary>
    public static BsonDocument PagedAggregation(this IEnumerable<BsonDocument> profiles)
    {
        return profiles.Single(profile =>
            profile["command"].AsBsonDocument.TryGetValue("pipeline", out var pipeline)
            && pipeline.AsBsonArray.Any(stage => stage.AsBsonDocument.Contains("$limit")));
    }

    /// <summary>The profile of the one find among <paramref name="profiles"/>.</summary>
    public static BsonDocument FindCommand(this IEnumerable<BsonDocument> profiles)
    {
        return profiles.Single(profile => profile["command"].AsBsonDocument.Contains("find"));
    }

    public static bool SortedInMemory(this BsonDocument profile) => profile.GetValue("hasSortStage", false).ToBoolean();

    public static long DocumentsRead(this BsonDocument profile) => profile["docsExamined"].ToInt64();

    public static string Plan(this BsonDocument profile) => profile["planSummary"].AsString;
}
