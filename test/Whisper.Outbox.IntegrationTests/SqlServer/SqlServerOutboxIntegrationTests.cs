using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MediatR;
using Whisper;
using Whisper.Abstractions;
using Whisper.Outbox.IntegrationTests.Fakes;
using Whisper.Outbox.SqlServer;

namespace Whisper.Outbox.IntegrationTests.SqlServer;

[Collection(MsSqlCollection.Name)]
public sealed class SqlServerOutboxIntegrationTests : IAsyncLifetime
{
    private readonly MsSqlFixture _sqlFixture;
    private readonly TestDomainEventHandler _handler;
    private string _connectionString = null!;
    private IHost _host = null!;

    public SqlServerOutboxIntegrationTests(MsSqlFixture sqlFixture)
    {
        _sqlFixture = sqlFixture;
        _handler = new TestDomainEventHandler();
    }

    public async Task InitializeAsync()
    {
        _connectionString = await _sqlFixture.CreateDatabaseAsync();

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
                        o.AddSqlServer(new SqlOutboxConfiguration
                        {
                            ConnectionString = _connectionString,
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

        await _handler.WaitForFirstEvent(TimeSpan.FromSeconds(10));

        _handler.ReceivedEvents.Should().ContainSingle()
            .Which.Value.Should().Be(expectedValue);

        // Allow SetDispatchedAt to complete after the handler has fired
        OutboxRow? record = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var records = await ReadOutboxRows();
            record = records.SingleOrDefault();
            if (record?.DispatchedAtUtc is not null)
                break;
            await Task.Delay(50);
        }

        record.Should().NotBeNull();
        record!.DispatchedAtUtc.Should().NotBeNull();
    }

    private async Task<List<OutboxRow>> ReadOutboxRows()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, DispatchedAtUtc FROM [dbo].[outboxrecords]";

        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new OutboxRow(
                reader.GetGuid(0),
                await reader.IsDBNullAsync(1) ? null : reader.GetFieldValue<DateTimeOffset>(1)));
        }

        return rows;
    }

    private sealed record OutboxRow(Guid Id, DateTimeOffset? DispatchedAtUtc);
}
