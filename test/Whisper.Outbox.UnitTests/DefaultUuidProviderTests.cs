namespace Whisper.Outbox.UnitTests;
public class DefaultUuidProviderTests
{
    // Outbox dispatch order is Id order, and Mongo sorts Standard-representation GUIDs in the same order as their
    // string form, so consecutive ids must increase in string order.
    [Fact]
    public void Provide_ProvidesIdsThatIncreaseInSortOrder()
    {
        var sut = new DefaultUuidProvider();

        var ids = Enumerable.Range(0, 10_000).Select(_ => sut.Provide().ToString()).ToArray();

        ids.Should().NotContain(Guid.Empty.ToString());
        ids.Should().BeInAscendingOrder(StringComparer.Ordinal);
        ids.Should().OnlyHaveUniqueItems();
    }
}
