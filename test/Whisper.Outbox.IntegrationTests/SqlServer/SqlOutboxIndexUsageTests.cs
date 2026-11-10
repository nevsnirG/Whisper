using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.SqlServer;

namespace Whisper.Outbox.IntegrationTests.SqlServer;

/// <summary>
/// An outbox table is mostly dispatched records, with a handful waiting. These tests fill it that way and check that
/// the two queries the worker repeats forever read their index and not the whole table: SQL Server's own usage
/// counters must show no scan of the table for them.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class SqlOutboxIndexUsageTests(MsSqlFixture sqlFixture)
{
    private const int DispatchedRecords = 20_000;
    private const int PendingRecords = 5;

    // Regression guard: with a ready index that lacks its filter columns, this query scans the table on every poll.
    [Fact]
    public async Task ReadNextBatch_OnATableFullOfDispatchedRecords_DoesNotScanTheTable()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        await using var provider = await InstallAndFill(connectionString, retainDispatchedFor: null);
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var before = await SqlIndexes.Reads(connectionString, "outboxrecords");

        var batch = await store.ReadNextBatch(10, DateTimeOffset.UtcNow, CancellationToken.None);

        var after = await SqlIndexes.Reads(connectionString, "outboxrecords");
        batch.Should().HaveCount(PendingRecords);
        after[SqlIndexes.Table].Since(before[SqlIndexes.Table]).Scans.Should().Be(0, "a poll must not read the whole table");
        var readyIndex = after["IX_Outbox_Ready_ByDue"].Since(before["IX_Outbox_Ready_ByDue"]);
        (readyIndex.Seeks + readyIndex.Scans).Should().BePositive("the poll reads the waiting records through the ready index");
    }

    // Nothing has expired: the state the cleanup finds on most of its runs.
    [Fact]
    public async Task DeleteDispatchedBefore_WithNothingExpired_DoesNotScanTheTable()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        await using var provider = await InstallAndFill(connectionString, retainDispatchedFor: TimeSpan.FromDays(7));
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var before = await SqlIndexes.Reads(connectionString, "outboxrecords");

        var deleted = await store.DeleteDispatchedBefore(DateTimeOffset.UtcNow.AddDays(-7), 1000, CancellationToken.None);

        var after = await SqlIndexes.Reads(connectionString, "outboxrecords");
        deleted.Should().Be(0);
        after[SqlIndexes.Table].Since(before[SqlIndexes.Table]).Scans.Should().Be(0, "the cleanup must not read the whole table");
        var dispatchedIndex = after["IX_Outbox_Dispatched_ByDispatchedAt"].Since(before["IX_Outbox_Dispatched_ByDispatchedAt"]);
        (dispatchedIndex.Seeks + dispatchedIndex.Scans).Should().BePositive("the cleanup finds expired records through the dispatched index");
    }

    // The cleanup first runs into a backlog and then keeps running once nothing is left to delete, which is where
    // it must still not read the whole table.
    [Fact]
    public async Task DeleteDispatchedBefore_AfterWorkingOffABacklog_DoesNotScanTheTableOnceNothingIsLeft()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        await using var provider = await InstallAndFill(connectionString, retainDispatchedFor: TimeSpan.FromDays(7));
        await Execute(connectionString, """
UPDATE TOP (15000) [dbo].[outboxrecords]
SET DispatchedAtUtc = DATEADD(DAY, -30, SYSUTCDATETIME())
WHERE DispatchedAtUtc IS NOT NULL;
""");
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);

        var deleted = 0;
        int deletedByThisCall;
        do
        {
            deletedByThisCall = await store.DeleteDispatchedBefore(cutoff, 1000, CancellationToken.None);
            deleted += deletedByThisCall;
        } while (deletedByThisCall > 0);

        var before = await SqlIndexes.Reads(connectionString, "outboxrecords");
        var deletedOnceNothingIsLeft = await store.DeleteDispatchedBefore(cutoff, 1000, CancellationToken.None);
        var after = await SqlIndexes.Reads(connectionString, "outboxrecords");

        deleted.Should().Be(15_000);
        deletedOnceNothingIsLeft.Should().Be(0);
        after[SqlIndexes.Table].Since(before[SqlIndexes.Table]).Scans.Should().Be(0, "the cleanup must not read the whole table");
    }

    private static async Task<ServiceProvider> InstallAndFill(string connectionString, TimeSpan? retainDispatchedFor)
    {
        var services = new ServiceCollection();
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Whispers).Assembly));
        services.AddWhisper(b =>
        {
            b.AddMediatR();
            b.AddOutbox(o => o
                .ConfigureWorker(w => w.RetainDispatchedFor = retainDispatchedFor)
                .AddSqlServer(new SqlOutboxConfiguration { ConnectionString = connectionString }));
        });
        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);

        await Execute(connectionString, $"""
INSERT INTO [dbo].[outboxrecords] (Id, EnqueuedAtUtc, DispatchedAtUtc, AssemblyQualifiedType, Payload)
SELECT TOP ({DispatchedRecords}) NEWID(), SYSUTCDATETIME(), SYSUTCDATETIME(), N'Some.Event, Some.Assembly', REPLICATE(N'x', 300)
FROM sys.all_objects a CROSS JOIN sys.all_objects b;

INSERT INTO [dbo].[outboxrecords] (Id, EnqueuedAtUtc, AssemblyQualifiedType, Payload)
SELECT TOP ({PendingRecords}) NEWID(), SYSUTCDATETIME(), N'Some.Event, Some.Assembly', REPLICATE(N'x', 300)
FROM sys.all_objects;
""");
        return provider;
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
