namespace Whisper.UnitTests;

public class WhispersConcurrencyTests
{
    [Fact(DisplayName = "Concurrent About calls inside a shared scope lose no events")]
    public async Task ConcurrentAboutCallsInsideSharedScopeLoseNoEvents()
    {
        const int workerCount = 8;
        const int eventsPerWorker = 1000;
        using var scope = Whispers.CreateScope();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                for (var i = 0; i < eventsPerWorker; i++)
                {
                    Whispers.About(new TestEvent());
                }
            }))
            .ToArray();

        gate.SetResult();
        await Task.WhenAll(workers);

        Whispers.GetAndClearEvents().Should().HaveCount(workerCount * eventsPerWorker);
    }

    [Fact(DisplayName = "Concurrent About and GetAndClearEvents deliver every event exactly once")]
    public async Task ConcurrentAboutAndGetAndClearEventsDeliverEveryEventExactlyOnce()
    {
        const int producerCount = 4;
        const int eventsPerProducer = 500;
        using var scope = Whispers.CreateScope();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var producers = Enumerable.Range(0, producerCount)
            .Select(producer => Task.Run(async () =>
            {
                await gate.Task;
                for (var seq = 0; seq < eventsPerProducer; seq++)
                {
                    Whispers.About(new TestEvent(producer, seq));
                }
            }))
            .ToArray();
        var producersCompleted = Task.WhenAll(producers);

        var consumer = Task.Run(async () =>
        {
            await gate.Task;
            var received = new List<IDomainEvent>();
            while (!producersCompleted.IsCompleted)
            {
                received.AddRange(Whispers.GetAndClearEvents());
                await Task.Yield();
            }

            received.AddRange(Whispers.GetAndClearEvents());
            return received;
        });

        gate.SetResult();
        await producersCompleted;
        var received = await consumer;

        var producedPairs = Enumerable.Range(0, producerCount)
            .SelectMany(producer => Enumerable.Range(0, eventsPerProducer).Select(seq => (producer, seq)))
            .ToArray();
        var receivedPairs = received.Cast<TestEvent>()
            .Select(domainEvent => (domainEvent.Producer, domainEvent.Seq))
            .OrderBy(pair => pair.Producer).ThenBy(pair => pair.Seq)
            .ToArray();

        receivedPairs.Should().HaveCount(producerCount * eventsPerProducer);
        receivedPairs.Should().Equal(producedPairs);
    }

    [Fact(DisplayName = "Peek during concurrent About returns consistent snapshots")]
    public async Task PeekDuringConcurrentAboutReturnsConsistentSnapshots()
    {
        const int producerCount = 4;
        const int eventsPerProducer = 500;
        const int total = producerCount * eventsPerProducer;
        using var scope = Whispers.CreateScope();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var producers = Enumerable.Range(0, producerCount)
            .Select(producer => Task.Run(async () =>
            {
                await gate.Task;
                for (var seq = 0; seq < eventsPerProducer; seq++)
                {
                    Whispers.About(new TestEvent(producer, seq));
                }
            }))
            .ToArray();
        var producersCompleted = Task.WhenAll(producers);

        var observer = Task.Run(async () =>
        {
            await gate.Task;
            var lengths = new List<int>();
            var sawNull = false;
            do
            {
                var snapshot = Whispers.Peek();
                sawNull |= snapshot.Any(domainEvent => domainEvent is null);
                lengths.Add(snapshot.Length);
                await Task.Yield();
            } while (!producersCompleted.IsCompleted);

            return (lengths, sawNull);
        });

        gate.SetResult();
        await producersCompleted;
        var (lengths, sawNull) = await observer;

        sawNull.Should().BeFalse();
        lengths.Should().OnlyContain(length => length >= 0 && length <= total);
        lengths.Should().BeInAscendingOrder();
        Whispers.Peek().Should().HaveCount(total);
    }

    [Fact(DisplayName = "Concurrent GetAndClearEvents calls drain each event at most once")]
    public async Task ConcurrentGetAndClearEventsDrainAtMostOnce()
    {
        const int totalEvents = 1000;
        const int drainerCount = 8;
        using var scope = Whispers.CreateScope();
        for (var seq = 0; seq < totalEvents; seq++)
        {
            Whispers.About(new TestEvent(0, seq));
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drainers = Enumerable.Range(0, drainerCount)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                return Whispers.GetAndClearEvents();
            }))
            .ToArray();

        gate.SetResult();
        var batches = await Task.WhenAll(drainers);

        var drainedSeqs = batches.SelectMany(batch => batch)
            .Cast<TestEvent>()
            .Select(domainEvent => domainEvent.Seq)
            .ToArray();

        drainedSeqs.Should().HaveCount(totalEvents);
        drainedSeqs.Should().OnlyHaveUniqueItems();
        drainedSeqs.Order().Should().Equal(Enumerable.Range(0, totalEvents));
    }

    private sealed record class TestEvent(int Producer = 0, int Seq = 0) : IDomainEvent;
}
