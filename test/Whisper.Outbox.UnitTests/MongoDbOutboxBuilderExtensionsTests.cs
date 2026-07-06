using Microsoft.Extensions.DependencyInjection;
using Whisper.Outbox.MongoDb;

namespace Whisper.Outbox.UnitTests;

public class MongoDbOutboxBuilderExtensionsTests
{
    private static readonly MongoDbOutboxConfiguration Configuration = new()
    {
        ConnectionString = "mongodb://fake:27017",
        DatabaseName = "test",
    };

    [Fact]
    public void AddMongoDb_WithoutNServiceBusStorageSession_RegistersSingleScopedEmptyMongoSessionProvider()
    {
        var services = BuildServices(o => o.AddMongoDb(Configuration));

        var descriptor = services.Should().ContainSingle(s => s.ServiceType == typeof(IMongoSessionProvider)).Subject;
        descriptor.ImplementationType.Should().NotBeNull();
        descriptor.ImplementationType!.Name.Should().Be("EmptyMongoSessionProvider");
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UseNServiceBusStorageSession_InAnyOrderWithAddMongoDb_RegistersSingleScopedNServiceBusStorageSessionProvider(bool addMongoDbFirst)
    {
        var services = BuildServices(o =>
        {
            if (addMongoDbFirst)
            {
                o.AddMongoDb(Configuration).UseNServiceBusStorageSession();
            }
            else
            {
                o.UseNServiceBusStorageSession().AddMongoDb(Configuration);
            }
        });

        var descriptor = services.Should().ContainSingle(s => s.ServiceType == typeof(IMongoSessionProvider)).Subject;
        descriptor.ImplementationType.Should().NotBeNull();
        descriptor.ImplementationType!.Name.Should().Be("NServiceBusStorageSessionProvider");
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    private static IServiceCollection BuildServices(Action<IOutboxBuilder> configure)
    {
        var services = new ServiceCollection() as IServiceCollection;
        services.AddOptions();
        services.AddWhisper(b => b.AddOutbox(configure));
        return services;
    }
}
