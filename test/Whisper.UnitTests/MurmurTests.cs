namespace Whisper.UnitTests;
public class MurmurTests
{
    [Fact(DisplayName = "Whispers added can be retrieved")]
    public void AddedWhisperToMurmurCanBeRetrieved()
    {
        var murmur = new Murmur
        {
            new TestEvent()
        };

        murmur.ToArray().Should().HaveCount(1);
    }

    private sealed record class TestEvent(string Value = "") : IDomainEvent;
}
