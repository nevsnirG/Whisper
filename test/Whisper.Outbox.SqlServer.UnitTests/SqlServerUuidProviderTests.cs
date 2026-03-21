namespace Whisper.Outbox.SqlServer.UnitTests;

public class SqlServerUuidProviderTests
{
    [Fact]
    public void Provide_ReturnsNonEmptyGuid()
    {
        var sut = new SqlServerUuidProvider();

        var guid = sut.Provide();

        guid.Should().NotBeEmpty();
    }

    [Fact]
    public void Provide_ReturnsSequentialGuids()
    {
        var sut = new SqlServerUuidProvider();

        var guid1 = sut.Provide();
        var guid2 = sut.Provide();

        guid1.Should().NotBe(guid2);
    }
}
