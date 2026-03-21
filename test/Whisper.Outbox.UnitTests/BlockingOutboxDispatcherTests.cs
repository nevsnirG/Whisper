using Whisper.Abstractions;

namespace Whisper.Outbox.UnitTests;

public class BlockingOutboxDispatcherTests
{
    [Fact]
    public async Task Dispatch_WaitsForAwaiterBeforeDelegating()
    {
        var awaiter = new OutboxInstallerAwaiter();
        var inner = Substitute.For<IDispatchDomainEvents>();
        var sut = new BlockingOutboxDispatcher(awaiter, inner);
        var domainEvent = new TestEvent();

        var dispatchTask = sut.Dispatch(domainEvent, CancellationToken.None);

        // Should not have dispatched yet
        await inner.DidNotReceiveWithAnyArgs().Dispatch(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
        dispatchTask.IsCompleted.Should().BeFalse();

        // Signal completion
        awaiter.SignalCompletion();
        await dispatchTask;

        // Now it should have dispatched
        await inner.Received(1).Dispatch(domainEvent, CancellationToken.None);
    }

    [Fact]
    public async Task DispatchBatch_WaitsForAwaiterBeforeDelegating()
    {
        var awaiter = new OutboxInstallerAwaiter();
        var inner = Substitute.For<IDispatchDomainEvents>();
        var sut = new BlockingOutboxDispatcher(awaiter, inner);
        var domainEvents = new IDomainEvent[] { new TestEvent() };

        var dispatchTask = sut.Dispatch(domainEvents, CancellationToken.None);

        await inner.DidNotReceiveWithAnyArgs().Dispatch(Arg.Any<IDomainEvent[]>(), Arg.Any<CancellationToken>());
        dispatchTask.IsCompleted.Should().BeFalse();

        awaiter.SignalCompletion();
        await dispatchTask;

        await inner.Received(1).Dispatch(domainEvents, CancellationToken.None);
    }

    [Fact]
    public async Task Dispatch_WhenAwaiterAlreadyReady_DelegatesImmediately()
    {
        var awaiter = new OutboxInstallerAwaiter();
        awaiter.SignalCompletion();
        var inner = Substitute.For<IDispatchDomainEvents>();
        var sut = new BlockingOutboxDispatcher(awaiter, inner);
        var domainEvent = new TestEvent();

        await sut.Dispatch(domainEvent, CancellationToken.None);

        await inner.Received(1).Dispatch(domainEvent, CancellationToken.None);
    }

    [Fact]
    public async Task Dispatch_WhenCancelled_ThrowsOperationCanceledException()
    {
        var awaiter = new OutboxInstallerAwaiter();
        var inner = Substitute.For<IDispatchDomainEvents>();
        var sut = new BlockingOutboxDispatcher(awaiter, inner);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => sut.Dispatch(new TestEvent(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await inner.DidNotReceiveWithAnyArgs().Dispatch(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    private record TestEvent() : IDomainEvent;
}
