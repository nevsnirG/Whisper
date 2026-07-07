using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MediatR;
using Whisper.Abstractions;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.IntegrationTests.Fakes;
using Whisper.Outbox.SqlServer;

namespace Whisper.Outbox.IntegrationTests.SqlServer;

[Collection(MsSqlCollection.Name)]
public sealed class SqlServerRecoverabilityEndToEndTests : IAsyncLifetime
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly MsSqlFixture _sqlFixture;
    private readonly ToggleFailingDomainEventHandler _handler = new();
    private string _connectionString = null!;
    private IHost _host = null!;

    public SqlServerRecoverabilityEndToEndTests(MsSqlFixture sqlFixture)
    {
        _sqlFixture = sqlFixture;
    }

    public async Task InitializeAsync()
    {
        _connectionString = await _sqlFixture.CreateDatabaseAsync();

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddMediatR(cfg =>
                {
                    cfg.RegisterServicesFromAssembly(typeof(Whispers).Assembly);
                });
                services.AddSingleton<INotificationHandler<TestDomainEvent>>(_handler);

                services.AddWhisper(b =>
                {
                    b.AddMediatR();
                    b.AddOutbox(o =>
                    {
                        o.ConfigureWorker(w => w.PollingIntervalMs = 50);
                        // A single total attempt: the first failure permanently fails the record.
                        o.ConfigureRecoverability(r => r.MaxRetries = 1);
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
    public async Task FailedRecord_RetriedThroughManagementStore_IsRedispatched()
    {
        using (var scope = _host.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatchDomainEvents>();
            await dispatcher.Dispatch(new TestDomainEvent("e2e"), CancellationToken.None);
        }

        // The handler throws and MaxRetries is 1, so the worker permanently fails the record
        // and persists the exception.
        var failed = await WaitForRow(r => r.FailedAtUtc is not null);
        failed.LastError.Should().Contain(ToggleFailingDomainEventHandler.FailureMessage);
        failed.LastErrorAtUtc.Should().NotBeNull();
        failed.DispatchedAtUtc.Should().BeNull();

        _handler.StopFailing();
        var managementStore = _host.Services.GetRequiredService<IOutboxManagementStore>();
        (await managementStore.Retry(failed.Id, CancellationToken.None)).Should().BeTrue();

        await _handler.WaitForFirstSuccess(Deadline);
        var dispatched = await WaitForRow(r => r.DispatchedAtUtc is not null);
        dispatched.FailedAtUtc.Should().BeNull();
        dispatched.LastError.Should().NotBeNull("a successful dispatch keeps the error audit trail");
    }

    private async Task<OutboxRow> WaitForRow(Func<OutboxRow, bool> condition)
    {
        OutboxRow? last = null;
        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            last = await ReadSingleRow();
            if (last is not null && condition(last))
                return last;
            await Task.Delay(50);
        }

        throw new TimeoutException($"The outbox record did not reach the expected state within {Deadline}. Last observed row: {last?.ToString() ?? "<none>"}.");
    }

    private async Task<OutboxRow?> ReadSingleRow()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT Id, DispatchedAtUtc, FailedAtUtc, LastError, LastErrorAtUtc
FROM [dbo].[outboxrecords]";

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new OutboxRow(
            reader.GetGuid(0),
            await reader.IsDBNullAsync(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            await reader.IsDBNullAsync(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            await reader.IsDBNullAsync(3) ? null : reader.GetString(3),
            await reader.IsDBNullAsync(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
    }

    private sealed record OutboxRow(
        Guid Id,
        DateTimeOffset? DispatchedAtUtc,
        DateTimeOffset? FailedAtUtc,
        string? LastError,
        DateTimeOffset? LastErrorAtUtc);
}
