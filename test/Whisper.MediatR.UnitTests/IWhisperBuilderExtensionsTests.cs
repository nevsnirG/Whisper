using Whisper.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Whisper.MediatR.UnitTests;

public class IWhisperBuilderExtensionsTests
{
    [Fact]
    public void AddMediatR_RegistersDispatcher()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddWhisper(b => b.AddMediatR());

        var registration = serviceCollection.Should().ContainSingle().Which;
        registration.ServiceType.Should().Be<IDispatchDomainEvents>();
        registration.ImplementationType.Should().Be<MediatorDispatcher>();
    }
}