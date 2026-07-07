using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NServiceBus.Storage.MongoDB;
using Whisper.Outbox.Abstractions;
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
    public void AddMongoDb_Alone_DoesNotRegisterMongoSessionProvider()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration));
        using var scope = serviceProvider.CreateScope();

        scope.ServiceProvider.GetService<IMongoSessionProvider>().Should().BeNull();
    }

    [Fact]
    public void AddMongoDb_ResolvesMongoDbOutboxStore()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration));
        using var scope = serviceProvider.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        store.Should().BeOfType<MongoDbOutboxStore>();
    }

    [Fact]
    public void UseMongoSessionProvider_ResolvesInterfaceToRegisteredProvider()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration).UseMongoSessionProvider<FirstSessionProvider>());
        using var scope = serviceProvider.CreateScope();

        var sessionProvider = scope.ServiceProvider.GetRequiredService<IMongoSessionProvider>();

        sessionProvider.Should().BeOfType<FirstSessionProvider>();
    }

    [Fact]
    public void UseMongoSessionProvider_InterfaceAndConcreteTypeResolveToSameInstanceWithinScope()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration).UseMongoSessionProvider<FirstSessionProvider>());
        using var scope = serviceProvider.CreateScope();

        var viaInterface = scope.ServiceProvider.GetRequiredService<IMongoSessionProvider>();
        var viaConcreteType = scope.ServiceProvider.GetRequiredService<FirstSessionProvider>();

        viaInterface.Should().BeSameAs(viaConcreteType);
    }

    [Fact]
    public void UseMongoSessionProvider_ResolvesDifferentInstancesAcrossScopes()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration).UseMongoSessionProvider<FirstSessionProvider>());
        using var firstScope = serviceProvider.CreateScope();
        using var secondScope = serviceProvider.CreateScope();

        var firstInstance = firstScope.ServiceProvider.GetRequiredService<IMongoSessionProvider>();
        var secondInstance = secondScope.ServiceProvider.GetRequiredService<IMongoSessionProvider>();

        firstInstance.Should().NotBeSameAs(secondInstance);
    }

    [Fact]
    public void UseMongoSessionProvider_FactoryOverload_ResolvesInstanceCreatedByFactory()
    {
        var created = new List<FirstSessionProvider>();
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration).UseMongoSessionProvider(_ =>
        {
            var instance = new FirstSessionProvider();
            created.Add(instance);
            return instance;
        }));
        using var scope = serviceProvider.CreateScope();

        var sessionProvider = scope.ServiceProvider.GetRequiredService<IMongoSessionProvider>();

        created.Should().ContainSingle().Which.Should().BeSameAs(sessionProvider);
    }

    [Fact]
    public void UseMongoSessionProvider_CalledTwiceWithDifferentProviders_LastProviderWinsWithSingleRegistration()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration)
            .UseMongoSessionProvider<FirstSessionProvider>()
            .UseMongoSessionProvider<SecondSessionProvider>());
        using var scope = serviceProvider.CreateScope();

        var sessionProviders = scope.ServiceProvider.GetServices<IMongoSessionProvider>();

        sessionProviders.Should().ContainSingle().Which.Should().BeOfType<SecondSessionProvider>();
    }

    [Fact]
    public void UseMongoSessionProvider_GenericThenFactoryOverload_FactoryRegistrationWins()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration)
            .UseMongoSessionProvider<FirstSessionProvider>()
            .UseMongoSessionProvider(_ => new SecondSessionProvider()));
        using var scope = serviceProvider.CreateScope();

        var sessionProviders = scope.ServiceProvider.GetServices<IMongoSessionProvider>();

        sessionProviders.Should().ContainSingle().Which.Should().BeOfType<SecondSessionProvider>();
    }

    [Fact]
    public void UseMongoSessionProvider_FactoryThenGenericOverload_GenericRegistrationWins()
    {
        var created = new List<FirstSessionProvider>();
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration)
            .UseMongoSessionProvider(_ =>
            {
                var instance = new FirstSessionProvider();
                created.Add(instance);
                return instance;
            })
            .UseMongoSessionProvider<FirstSessionProvider>());
        using var scope = serviceProvider.CreateScope();

        var sessionProvider = scope.ServiceProvider.GetRequiredService<IMongoSessionProvider>();

        sessionProvider.Should().BeOfType<FirstSessionProvider>();
        created.Should().BeEmpty("the generic overload replaces the factory registration with a container-constructed one");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UseMongoSessionProvider_InAnyOrderWithAddMongoDb_RegisteredProviderWins(bool addMongoDbFirst)
    {
        using var serviceProvider = BuildServiceProvider(o =>
        {
            if (addMongoDbFirst)
                o.AddMongoDb(Configuration).UseMongoSessionProvider<FirstSessionProvider>();
            else
                o.UseMongoSessionProvider<FirstSessionProvider>().AddMongoDb(Configuration);
        });
        using var scope = serviceProvider.CreateScope();

        var sessionProvider = scope.ServiceProvider.GetRequiredService<IMongoSessionProvider>();

        sessionProvider.Should().BeOfType<FirstSessionProvider>();
    }

    [Fact]
    public void UseMongoSessionProvider_ProviderImplementingInitializer_ResolvesBothInterfacesToSameInstanceWithinScope()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration).UseMongoSessionProvider<InitializableSessionProvider>());
        using var scope = serviceProvider.CreateScope();

        var sessionProvider = scope.ServiceProvider.GetRequiredService<IMongoSessionProvider>();
        var initializer = scope.ServiceProvider.GetRequiredService<IMongoSessionProviderInitializer>();

        initializer.Should().BeSameAs(sessionProvider);
    }

    [Fact]
    public void UseMongoSessionProvider_ProviderWithoutInitializer_DoesNotRegisterInitializer()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration).UseMongoSessionProvider<FirstSessionProvider>());
        using var scope = serviceProvider.CreateScope();

        scope.ServiceProvider.GetService<IMongoSessionProviderInitializer>().Should().BeNull();
    }

    [Fact]
    public void UseMongoSessionProvider_WithoutInitializerAfterHostManagedMongoSession_RemovesStaleInitializerRegistration()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration)
            .UseHostManagedMongoSession()
            .UseMongoSessionProvider<FirstSessionProvider>());
        using var scope = serviceProvider.CreateScope();

        scope.ServiceProvider.GetService<IMongoSessionProviderInitializer>().Should().BeNull();
    }

    [Fact]
    public void UseHostManagedMongoSession_AfterProviderWithoutInitializer_ResolvesInitializerToHostManagedInstance()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration)
            .UseMongoSessionProvider<FirstSessionProvider>()
            .UseHostManagedMongoSession());
        using var scope = serviceProvider.CreateScope();

        var sessionProvider = scope.ServiceProvider.GetRequiredService<IMongoSessionProvider>();
        var initializer = scope.ServiceProvider.GetRequiredService<IMongoSessionProviderInitializer>();

        sessionProvider.Should().BeOfType<MongoSessionProvider>();
        initializer.Should().BeSameAs(sessionProvider);
    }

    [Fact]
    public void UseHostManagedMongoSession_ResolvesProviderAndInitializerToSameHostManagedInstance()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration).UseHostManagedMongoSession());
        using var scope = serviceProvider.CreateScope();

        var sessionProvider = scope.ServiceProvider.GetRequiredService<IMongoSessionProvider>();
        var initializer = scope.ServiceProvider.GetRequiredService<IMongoSessionProviderInitializer>();

        sessionProvider.Should().BeOfType<MongoSessionProvider>();
        initializer.Should().BeSameAs(sessionProvider);
    }

    [Fact]
    public void UseHostManagedMongoSession_AfterInitialize_ProviderExposesHostSession()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration).UseHostManagedMongoSession());
        using var scope = serviceProvider.CreateScope();
        var session = Substitute.For<IClientSessionHandle>();

        scope.ServiceProvider.GetRequiredService<IMongoSessionProviderInitializer>().Initialize(session);

        scope.ServiceProvider.GetRequiredService<IMongoSessionProvider>().Session.Should().BeSameAs(session);
    }

    [Fact]
    public void UseHostManagedMongoSession_FreshScope_HasNoSession()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddMongoDb(Configuration).UseHostManagedMongoSession());

        using (var initializedScope = serviceProvider.CreateScope())
        {
            initializedScope.ServiceProvider.GetRequiredService<IMongoSessionProviderInitializer>().Initialize(Substitute.For<IClientSessionHandle>());
        }

        using var freshScope = serviceProvider.CreateScope();
        freshScope.ServiceProvider.GetRequiredService<IMongoSessionProvider>().Session.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UseNServiceBusStorageSession_InAnyOrderWithAddMongoDb_ResolvesNServiceBusStorageSessionProvider(bool addMongoDbFirst)
    {
        using var serviceProvider = BuildServiceProvider(o =>
        {
            if (addMongoDbFirst)
                o.AddMongoDb(Configuration).UseNServiceBusStorageSession();
            else
                o.UseNServiceBusStorageSession().AddMongoDb(Configuration);
        }, s => s.AddScoped(_ => Substitute.For<IMongoSynchronizedStorageSession>()));
        using var scope = serviceProvider.CreateScope();

        var sessionProviders = scope.ServiceProvider.GetServices<IMongoSessionProvider>();

        sessionProviders.Should().ContainSingle().Which.GetType().Name.Should().Be("NServiceBusStorageSessionProvider");
    }

    [Fact]
    public void UseNServiceBusStorageSession_ProviderExposesSessionFromNServiceBusStorageSession()
    {
        var session = Substitute.For<IClientSessionHandle>();
        var storageSession = Substitute.For<IMongoSynchronizedStorageSession>();
        storageSession.MongoSession.Returns(session);
        using var serviceProvider = BuildServiceProvider(
            o => o.AddMongoDb(Configuration).UseNServiceBusStorageSession(),
            s => s.AddScoped(_ => storageSession));
        using var scope = serviceProvider.CreateScope();

        var sessionProvider = scope.ServiceProvider.GetRequiredService<IMongoSessionProvider>();

        sessionProvider.Session.Should().BeSameAs(session);
    }

    [Fact]
    public void UseNServiceBusStorageSession_DoesNotRegisterInitializer()
    {
        using var serviceProvider = BuildServiceProvider(
            o => o.AddMongoDb(Configuration).UseNServiceBusStorageSession(),
            s => s.AddScoped(_ => Substitute.For<IMongoSynchronizedStorageSession>()));
        using var scope = serviceProvider.CreateScope();

        scope.ServiceProvider.GetService<IMongoSessionProviderInitializer>().Should().BeNull();
    }

    private static IServiceCollection BuildServices(Action<IOutboxBuilder> configure, Action<IServiceCollection>? postConfigure = null)
    {
        var services = new ServiceCollection() as IServiceCollection;
        services.AddOptions();
        services.AddWhisper(b => b.AddOutbox(configure));
        postConfigure?.Invoke(services);
        return services;
    }

    private static ServiceProvider BuildServiceProvider(Action<IOutboxBuilder> configure, Action<IServiceCollection>? postConfigure = null)
    {
        return BuildServices(configure, postConfigure)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class FirstSessionProvider : IMongoSessionProvider
    {
        public IClientSessionHandle? Session => null;
    }

    private sealed class SecondSessionProvider : IMongoSessionProvider
    {
        public IClientSessionHandle? Session => null;
    }

    private sealed class InitializableSessionProvider : IMongoSessionProvider, IMongoSessionProviderInitializer
    {
        public IClientSessionHandle? Session { get; private set; }

        public void Initialize(IClientSessionHandle session) => Session = session;
    }
}
