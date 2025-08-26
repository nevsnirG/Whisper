using Hermes.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Hermes.MediatR.UnitTests;

public class IHermesBuilderExtensionsTests
{
    [Fact]
    public void AddMediatR_RegistersDispatcher()
    {
        var serviceCollection = new ServiceCollection() as IServiceCollection;
        serviceCollection.AddHermes(b => b.AddMediatR());

        var registration = serviceCollection.Should().ContainSingle().Which;
        registration.ServiceType.Should().Be<IDispatchDomainEvents>();
        registration.ImplementationType.Should().Be<MediatorDispatcher>();
    }
}