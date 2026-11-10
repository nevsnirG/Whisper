using Microsoft.Extensions.DependencyInjection;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.SqlServer;

namespace Whisper.Outbox.IntegrationTests.SqlServer;

[Collection(MsSqlCollection.Name)]
public sealed class SqlOutboxWorkerLockTests(MsSqlFixture sqlFixture)
{
    [Fact]
    public async Task TryAcquire_WhileAnotherInstanceHoldsTheLock_ReturnsNullUntilItIsReleased()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        await using var instanceA = BuildServiceProvider(connectionString);
        await using var instanceB = BuildServiceProvider(connectionString);
        var lockOfA = instanceA.GetRequiredService<IOutboxWorkerLock>();
        var lockOfB = instanceB.GetRequiredService<IOutboxWorkerLock>();

        var heldByA = await lockOfA.TryAcquire(CancellationToken.None);
        var refusedToB = await lockOfB.TryAcquire(CancellationToken.None);
        await heldByA!.DisposeAsync();
        var heldByB = await lockOfB.TryAcquire(CancellationToken.None);

        heldByA.Should().NotBeNull();
        refusedToB.Should().BeNull("instance A holds the lock");
        heldByB.Should().NotBeNull("instance A released the lock");
        await heldByB!.DisposeAsync();
    }

    // The lock is not re-entrant: a second poll of the same instance must not overlap the first either.
    [Fact]
    public async Task TryAcquire_WhileTheSameInstanceHoldsTheLock_ReturnsNull()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        await using var instance = BuildServiceProvider(connectionString);
        var workerLock = instance.GetRequiredService<IOutboxWorkerLock>();

        await using var held = await workerLock.TryAcquire(CancellationToken.None);
        var second = await workerLock.TryAcquire(CancellationToken.None);

        held.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public async Task TryAcquire_ForAnotherOutboxTable_IsNotAffected()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        await using var orders = BuildServiceProvider(connectionString, "orders_outbox");
        await using var billing = BuildServiceProvider(connectionString, "billing_outbox");

        await using var ordersLock = await orders.GetRequiredService<IOutboxWorkerLock>().TryAcquire(CancellationToken.None);
        await using var billingLock = await billing.GetRequiredService<IOutboxWorkerLock>().TryAcquire(CancellationToken.None);

        ordersLock.Should().NotBeNull();
        billingLock.Should().NotBeNull();
    }

    private static ServiceProvider BuildServiceProvider(string connectionString, string tableName = "outboxrecords")
    {
        var services = new ServiceCollection();
        services.AddWhisper(b => b.AddOutbox(o => o.AddSqlServer(new SqlOutboxConfiguration
        {
            ConnectionString = connectionString,
            TableName = tableName,
        })));
        return services.BuildServiceProvider();
    }
}
