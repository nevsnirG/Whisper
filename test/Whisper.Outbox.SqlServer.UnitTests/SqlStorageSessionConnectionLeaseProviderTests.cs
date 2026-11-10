using System.Data.Common;
using Microsoft.Data.SqlClient;
using NServiceBus.Persistence.Sql;
using Whisper.Outbox.SqlServer.NServiceBus;

namespace Whisper.Outbox.SqlServer.UnitTests;

public class SqlStorageSessionConnectionLeaseProviderTests
{
    // NServiceBus only opens its storage session while it handles a message. Resolved in any other scope (a plain
    // HTTP request that stores events, for one), the session has neither a connection nor a transaction.
    [Fact]
    public async Task Provide_SessionNotOpened_ReturnsNoLease()
    {
        var session = Substitute.For<ISqlStorageSession>();
        session.Connection.Returns((DbConnection)null!);
        session.Transaction.Returns((DbTransaction)null!);
        var sut = new SqlStorageSessionConnectionLeaseProvider(session);

        var lease = await sut.Provide(CancellationToken.None);

        lease.Should().BeNull("the store then opens a connection of its own, as it does without a provider");
    }

    [Fact]
    public async Task Provide_SessionOpened_LeasesItsConnection()
    {
        await using var connection = new SqlConnection("Server=fake;Database=test;Encrypt=False");
        var session = Substitute.For<ISqlStorageSession>();
        session.Connection.Returns(connection);
        session.Transaction.Returns((DbTransaction)null!);
        var sut = new SqlStorageSessionConnectionLeaseProvider(session);

        var lease = await sut.Provide(CancellationToken.None);

        lease.Should().NotBeNull();
        lease!.Connection.Should().BeSameAs(connection);
        lease.Transaction.Should().BeNull();
    }

    // A session on another ADO.NET provider must not quietly fall back to a separate connection: the outbox write
    // would then no longer be part of the message's transaction.
    [Fact]
    public async Task Provide_SessionOnAnotherAdoNetProvider_Throws()
    {
        var session = Substitute.For<ISqlStorageSession>();
        session.Connection.Returns(Substitute.For<DbConnection>());
        var sut = new SqlStorageSessionConnectionLeaseProvider(session);

        var act = async () => await sut.Provide(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidCastException>();
    }
}
