using Microsoft.AspNetCore.Http;
using Whisper;
using Whisper.Abstractions;

namespace Whisper.AspNetCore.UnitTests;

public class DomainEventDispatcherMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenNoEventsRaised_DoesNotDispatch()
    {
        var dispatcher = Substitute.For<IDispatchDomainEvents>();
        var context = new DefaultHttpContext();
        RequestDelegate next = _ => Task.CompletedTask;
        var sut = new DomainEventDispatcherMiddleware(next);

        await sut.InvokeAsync(context, [dispatcher]);

        await dispatcher.DidNotReceiveWithAnyArgs()
            .Dispatch(Arg.Any<IDomainEvent[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_WhenEventsRaised_DispatchesToAllDispatchers()
    {
        var dispatcher1 = Substitute.For<IDispatchDomainEvents>();
        var dispatcher2 = Substitute.For<IDispatchDomainEvents>();
        var context = new DefaultHttpContext();
        RequestDelegate next = _ =>
        {
            Whispers.About(new TestEvent("test"));
            return Task.CompletedTask;
        };
        var sut = new DomainEventDispatcherMiddleware(next);

        await sut.InvokeAsync(context, [dispatcher1, dispatcher2]);

        await dispatcher1.Received(1)
            .Dispatch(Arg.Is<IDomainEvent[]>(e => e.Length == 1), CancellationToken.None);
        await dispatcher2.Received(1)
            .Dispatch(Arg.Is<IDomainEvent[]>(e => e.Length == 1), CancellationToken.None);
    }

    [Fact]
    public async Task InvokeAsync_EventsRaisedInScope_AreIsolatedFromOuterScope()
    {
        Whispers.About(new TestEvent("outer"));

        var dispatcher = Substitute.For<IDispatchDomainEvents>();
        var context = new DefaultHttpContext();
        RequestDelegate next = _ =>
        {
            Whispers.About(new TestEvent("inner"));
            return Task.CompletedTask;
        };
        var sut = new DomainEventDispatcherMiddleware(next);

        await sut.InvokeAsync(context, [dispatcher]);

        await dispatcher.Received(1)
            .Dispatch(Arg.Is<IDomainEvent[]>(e =>
                e.Length == 1 && ((TestEvent)e[0]).Value == "inner"),
                CancellationToken.None);

        // Outer scope should still have its event
        Whispers.GetAndClearEvents().Should().HaveCount(1);
    }

    [Fact]
    public async Task InvokeAsync_RequestAbortedAfterEndpointRan_DispatchesWithoutCancellation()
    {
        using var requestAbortedCts = new CancellationTokenSource();
        var dispatcher = Substitute.For<IDispatchDomainEvents>();
        var context = new DefaultHttpContext { RequestAborted = requestAbortedCts.Token };
        RequestDelegate next = _ =>
        {
            Whispers.About(new TestEvent("test"));
            requestAbortedCts.Cancel(); // the client disconnects once the endpoint has saved its changes
            return Task.CompletedTask;
        };
        var sut = new DomainEventDispatcherMiddleware(next);

        await sut.InvokeAsync(context, [dispatcher]);

        await dispatcher.Received(1).Dispatch(Arg.Any<IDomainEvent[]>(), CancellationToken.None);
    }

    [Fact]
    public async Task InvokeAsync_EventRaisedWhileDispatching_ThrowsInsteadOfDroppingIt()
    {
        var dispatcher = Substitute.For<IDispatchDomainEvents>();
        dispatcher.Dispatch(Arg.Any<IDomainEvent[]>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Whispers.About(new TestEvent("raised-by-an-event-handler"));
                return Task.CompletedTask;
            });
        RequestDelegate next = _ =>
        {
            Whispers.About(new TestEvent("raised-by-the-endpoint"));
            return Task.CompletedTask;
        };
        var sut = new DomainEventDispatcherMiddleware(next);

        var act = () => sut.InvokeAsync(new DefaultHttpContext(), [dispatcher]);

        (await act.Should().ThrowAsync<DomainEventRaisedDuringDispatchException>())
            .Which.DomainEvent.Should().BeOfType<TestEvent>()
            .Which.Value.Should().Be("raised-by-an-event-handler");
    }

    private sealed record TestEvent(string Value) : IDomainEvent;
}
