using MediatR;

namespace Whisper.MediatR.UnitTests;
public class MediatorDispatcherTests
{
    private readonly MediatorDispatcher _sut;
    private readonly IMediator _mediatorMock;

    public MediatorDispatcherTests()
    {
        _mediatorMock = Substitute.For<IMediator>();
        _sut = new(_mediatorMock);
    }

    [Fact]
    public async Task Dispatch_DispatchesDomainEvent()
    {
        var cancellationToken = new CancellationTokenSource().Token;
        var domainEvent = Substitute.For<IDomainEvent>();

        await _sut.Dispatch(domainEvent, cancellationToken);

        await _mediatorMock.Received(1).Publish(domainEvent, cancellationToken);
        _mediatorMock.ReceivedCalls().Should().HaveCount(1);
    }

    [Fact]
    public async Task Dispatch_DispatchesDomainEvents()
    {
        var cancellationToken = new CancellationTokenSource().Token;
        var domainEvents = new IDomainEvent[] { Substitute.For<IDomainEvent>(), Substitute.For<IDomainEvent>() };

        await _sut.Dispatch(domainEvents, cancellationToken);

        await _mediatorMock.Received(1).Publish(domainEvents[0], cancellationToken);
        await _mediatorMock.Received(1).Publish(domainEvents[1], cancellationToken);
        _mediatorMock.ReceivedCalls().Should().HaveCount(2);
    }

    [Fact]
    public async Task Dispatch_DomainEvents_PublishesNextOnlyAfterPreviousCompleted()
    {
        var firstPublish = new TaskCompletionSource();
        var domainEvents = new IDomainEvent[] { Substitute.For<IDomainEvent>(), Substitute.For<IDomainEvent>() };
        _mediatorMock.Publish(domainEvents[0], Arg.Any<CancellationToken>()).Returns(firstPublish.Task);

        var dispatch = _sut.Dispatch(domainEvents, CancellationToken.None);

        await _mediatorMock.Received(1).Publish(domainEvents[0], Arg.Any<CancellationToken>());
        await _mediatorMock.DidNotReceive().Publish(domainEvents[1], Arg.Any<CancellationToken>());

        firstPublish.SetResult();
        await dispatch;

        await _mediatorMock.Received(1).Publish(domainEvents[1], Arg.Any<CancellationToken>());
    }
}
