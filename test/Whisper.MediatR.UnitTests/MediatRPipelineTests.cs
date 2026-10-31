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
        var services = new ServiceCollection();
        services.AddLogging(); // MediatR 13's license check resolves an ILoggerFactory
        services.AddSingleton<PublishedNumbers>();
        services.AddMediatR(c => c.RegisterServicesFromAssemblyContaining<MediatRPipelineTests>());
        services.AddWhisper(b => b.AddMediatR());
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new RaiseNumbers(3));

        response.Should().Be(3);
        var published = provider.GetRequiredService<PublishedNumbers>();
        published.Numbers.Should().Equal(1, 2, 3);
        published.MaxConcurrentHandlers.Should().Be(1);
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
