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

    [Fact(DisplayName = "Concurrent adds are all retained by DrainAll")]
    public async Task ConcurrentAddsAreAllRetainedByDrainAll()
    {
        const int adderCount = 8;
        const int addsPerAdder = 10_000;
        var murmur = new Murmur();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var adders = Enumerable.Range(0, adderCount)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                for (var i = 0; i < addsPerAdder; i++)
                {
                    murmur.Add(new TestEvent());
                }
            }))
            .ToArray();

        gate.SetResult();
        await Task.WhenAll(adders);

        murmur.DrainAll().Should().HaveCount(adderCount * addsPerAdder);
    }

    [Fact(DisplayName = "Snapshot never throws during concurrent adds")]
    public async Task SnapshotNeverThrowsDuringConcurrentAdds()
    {
        const int adderCount = 4;
        const int addsPerAdder = 5_000;
        const int total = adderCount * addsPerAdder;
        var murmur = new Murmur();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var adders = Enumerable.Range(0, adderCount)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                for (var i = 0; i < addsPerAdder; i++)
                {
                    murmur.Add(new TestEvent());
                }
            }))
            .ToArray();
        var addersCompleted = Task.WhenAll(adders);

        var observer = Task.Run(async () =>
        {
            await gate.Task;
            var lengths = new List<int>();
            var sawNull = false;
            do
            {
                var snapshot = murmur.Snapshot();
                sawNull |= snapshot.Any(domainEvent => domainEvent is null);
                lengths.Add(snapshot.Length);
                await Task.Yield();
            } while (!addersCompleted.IsCompleted);

            return (lengths, sawNull);
        });

        gate.SetResult();
        await addersCompleted;
        var (lengths, sawNull) = await observer;

        sawNull.Should().BeFalse();
        lengths.Should().OnlyContain(length => length >= 0 && length <= total);
        murmur.Snapshot().Should().HaveCount(total);
    }

    private sealed record class TestEvent(string Value = "") : IDomainEvent;
}
