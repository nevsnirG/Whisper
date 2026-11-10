using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Whisper.MediatR.UnitTests;

// Runs Whisper inside a real MediatR pipeline, so the run with -p:MediatRVersion=x.y.z exercises
// the behavior against that version's RequestHandlerDelegate and the dispatcher against its Publish.
public class MediatRPipelineTests
{
    [Fact]
    public async Task Send_EventsRaisedByHandler_ArePublishedInRaisedOrderOneAtATime()
    {
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new RaiseNumbers(3));

        response.Should().Be(3);
        var published = provider.GetRequiredService<PublishedNumbers>();
        published.Numbers.Should().Equal(1, 2, 3);
        published.MaxConcurrentHandlers.Should().Be(1);
    }

    [Fact]
    public async Task Send_NotificationHandlerRaisesAnEvent_FailsInsteadOfDroppingIt()
    {
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<IMediator>().Send(new PlaceOrder(HandlerRaisesDirectly: true));

        (await act.Should().ThrowAsync<DomainEventRaisedDuringDispatchException>())
            .Which.DomainEvent.Should().BeOfType<StockReserved>();
        provider.GetRequiredService<Journal>().Entries.Should().BeEmpty("the event raised by the handler is rejected, not dispatched");
    }

    // The supported way to react to an event with more work: send a command. It is a use case of its own with its
    // own scope, so the events it raises are dispatched when it completes.
    [Fact]
    public async Task Send_NotificationHandlerSendsACommand_ThatCommandsEventsAreDispatched()
    {
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new PlaceOrder(HandlerRaisesDirectly: false));

        provider.GetRequiredService<Journal>().Entries.Should().Equal("StockReserved");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(); // MediatR 13's license check resolves an ILoggerFactory
        services.AddSingleton<PublishedNumbers>();
        services.AddSingleton<Journal>();
        services.AddMediatR(c => c.RegisterServicesFromAssemblyContaining<MediatRPipelineTests>());
        services.AddWhisper(b => b.AddMediatR());
        return services.BuildServiceProvider();
    }

    public sealed record PlaceOrder(bool HandlerRaisesDirectly) : IRequest<int>;

    public sealed record OrderPlaced(bool HandlerRaisesDirectly) : IDomainEvent, INotification;

    public sealed record ReserveStock : IRequest<int>;

    public sealed record StockReserved : IDomainEvent, INotification;

    public sealed class PlaceOrderHandler : IRequestHandler<PlaceOrder, int>
    {
        public Task<int> Handle(PlaceOrder request, CancellationToken cancellationToken)
        {
            Whispers.About(new OrderPlaced(request.HandlerRaisesDirectly));
            return Task.FromResult(0);
        }
    }

    public sealed class OrderPlacedHandler(IMediator mediator) : INotificationHandler<OrderPlaced>
    {
        public async Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
        {
            if (notification.HandlerRaisesDirectly)
                Whispers.About(new StockReserved());
            else
                await mediator.Send(new ReserveStock(), cancellationToken);
        }
    }

    public sealed class ReserveStockHandler : IRequestHandler<ReserveStock, int>
    {
        public Task<int> Handle(ReserveStock request, CancellationToken cancellationToken)
        {
            Whispers.About(new StockReserved());
            return Task.FromResult(0);
        }
    }

    public sealed class StockReservedHandler(Journal journal) : INotificationHandler<StockReserved>
    {
        public Task Handle(StockReserved notification, CancellationToken cancellationToken)
        {
            journal.Add(nameof(StockReserved));
            return Task.CompletedTask;
        }
    }

    public sealed class Journal
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

        public void Add(string entry)
        {
            lock (_entries)
                _entries.Add(entry);
        }
    }

    public sealed record RaiseNumbers(int Count) : IRequest<int>;

    public sealed record NumberRaised(int Number) : IDomainEvent, INotification;

    public sealed class RaiseNumbersHandler : IRequestHandler<RaiseNumbers, int>
    {
        public Task<int> Handle(RaiseNumbers request, CancellationToken cancellationToken)
        {
            for (var number = 1; number <= request.Count; number++)
                Whispers.About(new NumberRaised(number));

            return Task.FromResult(request.Count);
        }
    }

    public sealed class NumberRaisedHandler(PublishedNumbers published) : INotificationHandler<NumberRaised>
    {
        public async Task Handle(NumberRaised notification, CancellationToken cancellationToken)
        {
            published.Enter();
            // Later numbers finish faster, so concurrent publishing would complete out of order.
            await Task.Delay(TimeSpan.FromMilliseconds(20 * (4 - notification.Number)), cancellationToken);
            published.Exit(notification.Number);
        }
    }

    public sealed class PublishedNumbers
    {
        private readonly object _gate = new();
        private int _running;

        public List<int> Numbers { get; } = [];
        public int MaxConcurrentHandlers { get; private set; }

        public void Enter()
        {
            lock (_gate)
                MaxConcurrentHandlers = Math.Max(MaxConcurrentHandlers, ++_running);
        }

        public void Exit(int number)
        {
            lock (_gate)
            {
                _running--;
                Numbers.Add(number);
            }
        }
    }
}
