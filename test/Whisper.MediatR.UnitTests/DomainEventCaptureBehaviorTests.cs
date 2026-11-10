using MediatR;
using Whisper.Abstractions;

namespace Whisper.MediatR.UnitTests;

public class DomainEventCaptureBehaviorTests
{
    [Fact]
    public async Task Handle_WhenEventsRaised_DispatchesAfterHandler()
    {
        var dispatcher = Substitute.For<IDispatchDomainEvents>();
        var sut = new DomainEventCaptureBehavior<TestRequest, string>([dispatcher]);

        var result = await sut.Handle(new TestRequest(), Next.From(() =>
        {
            Whispers.About(new TestEvent("raised-in-handler"));
            return Task.FromResult("response");
        }), CancellationToken.None);

        result.Should().Be("response");
        await dispatcher.Received(1).Dispatch(
            Arg.Is<IDomainEvent[]>(e => e.Length == 1 && ((TestEvent)e[0]).Value == "raised-in-handler"),
            CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenNoEventsRaised_DoesNotDispatch()
    {
        var dispatcher = Substitute.For<IDispatchDomainEvents>();
        var sut = new DomainEventCaptureBehavior<TestRequest, string>([dispatcher]);

        await sut.Handle(new TestRequest(), Next.From(() => Task.FromResult("response")), CancellationToken.None);

        await dispatcher.DidNotReceiveWithAnyArgs()
            .Dispatch(Arg.Any<IDomainEvent[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EventsAreIsolatedFromOuterScope()
    {
        Whispers.About(new TestEvent("outer"));

        var dispatcher = Substitute.For<IDispatchDomainEvents>();
        var sut = new DomainEventCaptureBehavior<TestRequest, string>([dispatcher]);

        await sut.Handle(new TestRequest(), Next.From(() =>
        {
            Whispers.About(new TestEvent("inner"));
            return Task.FromResult("response");
        }), CancellationToken.None);

        // Only inner event dispatched
        await dispatcher.Received(1).Dispatch(
            Arg.Is<IDomainEvent[]>(e => e.Length == 1 && ((TestEvent)e[0]).Value == "inner"),
            CancellationToken.None);

        // Outer event still available
        Whispers.GetAndClearEvents().Should().HaveCount(1);
    }

    [Fact]
    public async Task Handle_DispatchesToMultipleDispatchers()
    {
        var dispatcher1 = Substitute.For<IDispatchDomainEvents>();
        var dispatcher2 = Substitute.For<IDispatchDomainEvents>();
        var sut = new DomainEventCaptureBehavior<TestRequest, string>([dispatcher1, dispatcher2]);

        await sut.Handle(new TestRequest(), Next.From(() =>
        {
            Whispers.About(new TestEvent("test"));
            return Task.FromResult("response");
        }), CancellationToken.None);

        await dispatcher1.Received(1).Dispatch(Arg.Any<IDomainEvent[]>(), CancellationToken.None);
        await dispatcher2.Received(1).Dispatch(Arg.Any<IDomainEvent[]>(), CancellationToken.None);
    }

    [Fact]
    public async Task Handle_RequestCancelledAfterHandlerRan_DispatchesWithoutCancellation()
    {
        using var requestCts = new CancellationTokenSource();
        var dispatcher = Substitute.For<IDispatchDomainEvents>();
        var sut = new DomainEventCaptureBehavior<TestRequest, string>([dispatcher]);

        await sut.Handle(new TestRequest(), Next.From(() =>
        {
            Whispers.About(new TestEvent("test"));
            requestCts.Cancel(); // e.g. the client disconnects once the handler has saved its changes
            return Task.FromResult("response");
        }), requestCts.Token);

        await dispatcher.Received(1).Dispatch(Arg.Any<IDomainEvent[]>(), CancellationToken.None);
    }

    [Fact]
    public async Task Handle_EventRaisedWhileDispatching_ThrowsInsteadOfDroppingIt()
    {
        var dispatcher = Substitute.For<IDispatchDomainEvents>();
        dispatcher.Dispatch(Arg.Any<IDomainEvent[]>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Whispers.About(new TestEvent("raised-by-an-event-handler"));
                return Task.CompletedTask;
            });
        var sut = new DomainEventCaptureBehavior<TestRequest, string>([dispatcher]);

        var act = () => sut.Handle(new TestRequest(), Next.From(() =>
        {
            Whispers.About(new TestEvent("raised-in-handler"));
            return Task.FromResult("response");
        }), CancellationToken.None);

        (await act.Should().ThrowAsync<DomainEventRaisedDuringDispatchException>())
            .Which.DomainEvent.Should().BeOfType<TestEvent>()
            .Which.Value.Should().Be("raised-by-an-event-handler");
    }

    [Fact]
    public async Task Handle_AfterDispatching_EventsCanBeRaisedAgain()
    {
        var dispatcher = Substitute.For<IDispatchDomainEvents>();
        var sut = new DomainEventCaptureBehavior<TestRequest, string>([dispatcher]);
        using var outerScope = Whispers.CreateScope();

        await sut.Handle(new TestRequest(), Next.From(() =>
        {
            Whispers.About(new TestEvent("raised-in-handler"));
            return Task.FromResult("response");
        }), CancellationToken.None);

        Whispers.About(new TestEvent("raised-after-the-request"));
        Whispers.GetAndClearEvents().Should().ContainSingle();
    }

#if MEDIATR_NEXT_TAKES_CANCELLATION_TOKEN
    // Only meaningful where RequestHandlerDelegate takes a token (MediatR 12.5+); the CI matrix runs it there.
    [Fact]
    public async Task Handle_PassesItsCancellationTokenToNext()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken received = default;
        var sut = new DomainEventCaptureBehavior<TestRequest, string>([]);

        await sut.Handle(new TestRequest(), t =>
        {
            received = t;
            return Task.FromResult("response");
        }, cts.Token);

        received.Should().Be(cts.Token);
    }
#endif

    private record TestRequest : IRequest<string>;
    private record TestEvent(string Value) : IDomainEvent;
}
