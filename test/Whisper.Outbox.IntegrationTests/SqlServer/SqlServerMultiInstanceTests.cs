using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.IntegrationTests.Fakes;
using Whisper.Outbox.SqlServer;

namespace Whisper.Outbox.IntegrationTests.SqlServer;

/// <summary>
/// Several instances of one application share its outbox. Their workers must take turns: left alone, they all read
/// the same pending records and each instance dispatches every one of them.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class SqlServerMultiInstanceTests(MsSqlFixture sqlFixture)
{
    private const int Events = 60;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task TwoInstancesStartingOnABacklog_DispatchEveryEventExactlyOnce()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        var handler = new CountingDomainEventHandler();
        using var instanceA = BuildHost(connectionString, handler);
        using var instanceB = BuildHost(connectionString, handler);
        await StoreBacklog(instanceA.Services);

        await Task.WhenAll(instanceA.StartAsync(), instanceB.StartAsync());
        await WaitUntilNothingIsPending(connectionString);
        await Task.WhenAll(instanceA.StopAsync(), instanceB.StopAsync());

        handler.TimesHandled.Should().HaveCount(Events);
        handler.TimesHandled.Where(handled => handled.Value != 1).Should().BeEmpty("each event is dispatched by one instance only");
    }

    private static IHost BuildHost(string connectionString, CountingDomainEventHandler handler)
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
                        o.AddSqlServer(new SqlOutboxConfiguration { ConnectionString = connectionString });
                    });
                });
            })
            .Build();
    }

    // Written before any worker runs, as after an outage: both instances find the whole backlog at their first poll.
    private static async Task StoreBacklog(IServiceProvider services)
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
    }

    private static async Task WaitUntilNothingIsPending(string connectionString)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM [dbo].[outboxrecords] WHERE DispatchedAtUtc IS NULL";
            if ((int)(await command.ExecuteScalarAsync())! == 0)
                return;

            await Task.Delay(50);
        }

        throw new TimeoutException($"The outbox still held pending records after {Deadline}.");
    }
}
