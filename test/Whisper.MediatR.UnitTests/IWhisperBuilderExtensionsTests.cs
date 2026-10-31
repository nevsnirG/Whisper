using MediatR;
using Whisper.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Whisper.MediatR.UnitTests;

public class IWhisperBuilderExtensionsTests
{
    [Fact]
    public void AddMediatR_RegistersDispatcherAndCaptureBehavior()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddWhisper(b => b.AddMediatR());

        serviceCollection.Should().Contain(s =>
            s.ServiceType == typeof(IDispatchDomainEvents)
            && s.ImplementationType == typeof(MediatorDispatcher));

        serviceCollection.Should().Contain(s =>
            s.ServiceType == typeof(IPipelineBehavior<,>)
            && s.ImplementationType == typeof(DomainEventCaptureBehavior<,>));
    }

    [Fact]
    public void AddMediatR_CalledTwice_RegistersDispatcherAndCaptureBehaviorOnce()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddWhisper(b =>
        {
            b.AddMediatR();
            b.AddMediatR();
        });

        serviceCollection.Should().ContainSingle(s =>
            s.ServiceType == typeof(IDispatchDomainEvents)
            && s.ImplementationType == typeof(MediatorDispatcher));

        serviceCollection.Should().ContainSingle(s =>
            s.ServiceType == typeof(IPipelineBehavior<,>)
            && s.ImplementationType == typeof(DomainEventCaptureBehavior<,>));
    }
}
