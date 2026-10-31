using MongoDB.Driver;
using Whisper.Outbox.MongoDb;

namespace Whisper.Outbox.UnitTests;

public class MongoSessionProviderTests
{
    [Fact]
    public void NewProvider_HasNoSessionAndReportsNotInTransaction()
    {
        IMongoSessionProvider sut = new MongoSessionProvider();

        sut.Session.Should().BeNull();
        sut.IsInTransaction.Should().BeFalse();
    }

    [Fact]
    public void Initialize_WithSessionInTransaction_ExposesSessionAndReportsInTransaction()
    {
        var session = Substitute.For<IClientSessionHandle>();
        session.IsInTransaction.Returns(true);
        var sut = new MongoSessionProvider();

        sut.Initialize(session);

        IMongoSessionProvider provider = sut;
        provider.Session.Should().BeSameAs(session);
        provider.IsInTransaction.Should().BeTrue();
    }

    [Fact]
    public void Initialize_WithSessionWithoutTransaction_ExposesSessionAndReportsNotInTransaction()
    {
        var session = Substitute.For<IClientSessionHandle>();
        session.IsInTransaction.Returns(false);
        var sut = new MongoSessionProvider();

        sut.Initialize(session);

        IMongoSessionProvider provider = sut;
        provider.Session.Should().BeSameAs(session);
        provider.IsInTransaction.Should().BeFalse();
    }
}
