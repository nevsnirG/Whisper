using MediatR;
using Microsoft.Extensions.Hosting;
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

        await dispatcher.Received(1).Dispatch(
            Arg.Any<IDomainEvent[]>(),
            Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task Handle_WithApplicationLifetime_DispatchesWithApplicationStoppingToken()
    {
        using var stoppingCts = new CancellationTokenSource();
        var applicationLifetime = Substitute.For<IHostApplicationLifetime>();
        applicationLifetime.ApplicationStopping.Returns(stoppingCts.Token);
        var dispatcher = Substitute.For<IDispatchDomainEvents>();
        var sut = new DomainEventCaptureBehavior<TestRequest, string>([dispatcher], applicationLifetime);

        await sut.Handle(new TestRequest(), Next.From(() =>
        {
            Whispers.About(new TestEvent("test"));
            return Task.FromResult("response");
        }), CancellationToken.None);

        await dispatcher.Received(1).Dispatch(Arg.Any<IDomainEvent[]>(), stoppingCts.Token);
    }

    private record TestRequest : IRequest<string>;
    private record TestEvent(string Value) : IDomainEvent;
}
