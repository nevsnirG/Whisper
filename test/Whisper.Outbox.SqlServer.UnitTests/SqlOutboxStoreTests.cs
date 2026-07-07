using Microsoft.Data.SqlClient;
using System.Data;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.SqlServer.UnitTests;

public class SqlOutboxStoreTests
{
    private const string ConnectionString = "Server=fake;Database=test;Connect Timeout=1;Encrypt=False";

    private static readonly SqlOutboxConfiguration Configuration = new()
    {
        ConnectionString = ConnectionString,
    };

    private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly OutboxFailure Failure = new("Simulated failure", Timestamp);

    private static readonly Dictionary<string, Func<IOutboxStore, Task>> OperationMap = new()
    {
        ["Add"] = store => store.Add(CreateRecord(), CancellationToken.None),
        ["AddBatch"] = store => store.Add(new[] { CreateRecord(), CreateRecord() }, CancellationToken.None),
        ["ReadNextBatch"] = store => store.ReadNextBatch(10, Timestamp, CancellationToken.None),
        ["SetDispatchedAt"] = store => store.SetDispatchedAt(CreateRecord(), Timestamp, CancellationToken.None),
        ["ScheduleRetry"] = store => store.ScheduleRetry(CreateRecord(), Failure, Timestamp, CancellationToken.None),
        ["SetFailedAt"] = store => store.SetFailedAt(CreateRecord(), Failure, CancellationToken.None),
    };

    public static TheoryData<string> Operations()
    {
        var data = new TheoryData<string>();
        foreach (var operation in OperationMap.Keys)
            data.Add(operation);
        return data;
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Operation_WithConnectionLeaseProvider_DoesNotOpenTheLeasedConnection(string operation)
    {
        await using var connection = new SqlConnection(ConnectionString);
        var provider = new StubConnectionLeaseProvider(new ConnectionLease(connection));
        var sut = new SqlOutboxStore(Configuration, provider);

        var act = () => OperationMap[operation](sut);

        // The leased connection was never opened by the host. The command must therefore fail
        // immediately with "requires an open and available Connection" - proving the store used
        // the lease as-is instead of opening (or reconnecting) it on the host's behalf.
        await act.Should().ThrowAsync<InvalidOperationException>();
        connection.State.Should().Be(ConnectionState.Closed);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Operation_WithConnectionLeaseProvider_DoesNotDisposeTheLeasedConnection(string operation)
    {
        await using var connection = new SqlConnection(ConnectionString);
        var disposed = false;
        connection.Disposed += (_, _) => disposed = true;
        var provider = new StubConnectionLeaseProvider(new ConnectionLease(connection));
        var sut = new SqlOutboxStore(Configuration, provider);

        var act = () => OperationMap[operation](sut);

        await act.Should().ThrowAsync<InvalidOperationException>();
        disposed.Should().BeFalse();
        // Disposing a SqlConnection resets its connection string; an intact value is a second
        // independent signal that the store did not dispose the host-owned lease.
        connection.ConnectionString.Should().Be(ConnectionString);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Operation_WithConnectionLeaseProvider_AcquiresExactlyOneLease(string operation)
    {
        await using var connection = new SqlConnection(ConnectionString);
        var provider = new StubConnectionLeaseProvider(new ConnectionLease(connection));
        var sut = new SqlOutboxStore(Configuration, provider);

        var act = () => OperationMap[operation](sut);

        await act.Should().ThrowAsync<InvalidOperationException>();
        provider.ProvideCallCount.Should().Be(1);
    }

    [Theory]
    [InlineData("Add")]
    [InlineData("ReadNextBatch")]
    public async Task Operation_WithoutConnectionLeaseProvider_OpensItsOwnConnection(string operation)
    {
        // Loopback port 1 refuses connections immediately - faster and more deterministic
        // than a fake hostname, which depends on environment DNS behavior.
        var configuration = new SqlOutboxConfiguration
        {
            ConnectionString = "Server=tcp:127.0.0.1,1;Database=test;Connect Timeout=1;Encrypt=False",
        };
        var sut = new SqlOutboxStore(configuration, null);

        var act = () => OperationMap[operation](sut);

        // With no lease provider the store owns the connection path: it creates its own connection
        // and tries to open it. Against the unreachable fake server that surfaces as a SqlException
        // from OpenAsync - not the InvalidOperationException an unopened leased connection produces.
        await act.Should().ThrowAsync<SqlException>();
    }

    private static OutboxRecord CreateRecord() => new()
    {
        Id = Guid.NewGuid(),
        EnqueuedAtUtc = Timestamp,
        AssemblyQualifiedType = "Whisper.Test.Event, Whisper.Test",
        Payload = "{}",
    };

    private sealed class StubConnectionLeaseProvider(ConnectionLease lease) : IConnectionLeaseProvider
    {
        public int ProvideCallCount { get; private set; }

        public ValueTask<ConnectionLease> Provide(CancellationToken cancellationToken)
        {
            ProvideCallCount++;
            return ValueTask.FromResult(lease);
        }
    }
}
