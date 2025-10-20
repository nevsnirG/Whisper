namespace Hermes.Outbox.UnitTests;
public class DefaultUuidProviderTests
{
    [Fact]
    public void Provide_ProvidesSequentialUuid()
    {
        var sut = new DefaultUuidProvider();

        var guid = sut.Provide();

        guid.Should().NotBeEmpty();
    }
}
