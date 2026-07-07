using Microsoft.Extensions.DependencyInjection;
using NServiceBus.Persistence.Sql;
using Whisper.Abstractions;
using Whisper.Outbox;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.SqlServer.UnitTests;

public class IOutboxBuilderExtensionsTests
{
    private static readonly SqlOutboxConfiguration Configuration = new()
    {
        ConnectionString = "Server=fake;Database=test;"
    };

    [Fact]
    public void AddSqlServer_RegistersRequiredServices()
    {
        var services = BuildServices(o => o.AddSqlServer(Configuration));

        services.Should().Contain(s => s.ServiceType == typeof(IOutboxStore) && s.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(s => s.ServiceType == typeof(IInstallOutbox) && s.ImplementationType == typeof(SqlOutboxInstaller));
        services.Should().Contain(s => s.ServiceType == typeof(IUuidProvider) && s.ImplementationType == typeof(SqlServerUuidProvider));
        services.Should().Contain(s => s.ServiceType == typeof(SqlOutboxConfiguration));
    }

    [Fact]
    public void AddSqlServer_ResolvesSqlOutboxStore()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddSqlServer(Configuration));
        using var scope = serviceProvider.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        store.Should().BeOfType<SqlOutboxStore>();
    }

    [Fact]
    public void AddSqlServer_Alone_DoesNotRegisterConnectionLeaseProvider()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddSqlServer(Configuration));
        using var scope = serviceProvider.CreateScope();

        scope.ServiceProvider.GetService<IConnectionLeaseProvider>().Should().BeNull();
    }

    [Fact]
    public void UseConnectionLeaseProvider_ResolvesInterfaceToRegisteredProvider()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddSqlServer(Configuration).UseConnectionLeaseProvider<FirstLeaseProvider>());
        using var scope = serviceProvider.CreateScope();

        var leaseProvider = scope.ServiceProvider.GetRequiredService<IConnectionLeaseProvider>();

        leaseProvider.Should().BeOfType<FirstLeaseProvider>();
    }

    [Fact]
    public void UseConnectionLeaseProvider_InterfaceAndConcreteTypeResolveToSameInstanceWithinScope()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddSqlServer(Configuration).UseConnectionLeaseProvider<FirstLeaseProvider>());
        using var scope = serviceProvider.CreateScope();

        var viaInterface = scope.ServiceProvider.GetRequiredService<IConnectionLeaseProvider>();
        var viaConcreteType = scope.ServiceProvider.GetRequiredService<FirstLeaseProvider>();

        viaInterface.Should().BeSameAs(viaConcreteType);
    }

    [Fact]
    public void UseConnectionLeaseProvider_ResolvesDifferentInstancesAcrossScopes()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddSqlServer(Configuration).UseConnectionLeaseProvider<FirstLeaseProvider>());
        using var firstScope = serviceProvider.CreateScope();
        using var secondScope = serviceProvider.CreateScope();

        var firstInstance = firstScope.ServiceProvider.GetRequiredService<IConnectionLeaseProvider>();
        var secondInstance = secondScope.ServiceProvider.GetRequiredService<IConnectionLeaseProvider>();

        firstInstance.Should().NotBeSameAs(secondInstance);
    }

    [Fact]
    public void UseConnectionLeaseProvider_FactoryOverload_ResolvesInstanceCreatedByFactory()
    {
        var created = new List<FirstLeaseProvider>();
        using var serviceProvider = BuildServiceProvider(o => o.AddSqlServer(Configuration).UseConnectionLeaseProvider(_ =>
        {
            var instance = new FirstLeaseProvider();
            created.Add(instance);
            return instance;
        }));
        using var scope = serviceProvider.CreateScope();

        var leaseProvider = scope.ServiceProvider.GetRequiredService<IConnectionLeaseProvider>();

        created.Should().ContainSingle().Which.Should().BeSameAs(leaseProvider);
    }

    [Fact]
    public void UseConnectionLeaseProvider_CalledTwiceWithDifferentProviders_LastProviderWinsWithSingleRegistration()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddSqlServer(Configuration)
            .UseConnectionLeaseProvider<FirstLeaseProvider>()
            .UseConnectionLeaseProvider<SecondLeaseProvider>());
        using var scope = serviceProvider.CreateScope();

        var leaseProviders = scope.ServiceProvider.GetServices<IConnectionLeaseProvider>();

        leaseProviders.Should().ContainSingle().Which.Should().BeOfType<SecondLeaseProvider>();
    }

    [Fact]
    public void UseConnectionLeaseProvider_GenericThenFactoryOverload_FactoryRegistrationWins()
    {
        using var serviceProvider = BuildServiceProvider(o => o.AddSqlServer(Configuration)
            .UseConnectionLeaseProvider<FirstLeaseProvider>()
            .UseConnectionLeaseProvider(_ => new SecondLeaseProvider()));
        using var scope = serviceProvider.CreateScope();

        var leaseProviders = scope.ServiceProvider.GetServices<IConnectionLeaseProvider>();

        leaseProviders.Should().ContainSingle().Which.Should().BeOfType<SecondLeaseProvider>();
    }

    [Fact]
    public void UseConnectionLeaseProvider_FactoryThenGenericOverload_GenericRegistrationWins()
    {
        var created = new List<FirstLeaseProvider>();
        using var serviceProvider = BuildServiceProvider(o => o.AddSqlServer(Configuration)
            .UseConnectionLeaseProvider(_ =>
            {
                var instance = new FirstLeaseProvider();
                created.Add(instance);
                return instance;
            })
            .UseConnectionLeaseProvider<FirstLeaseProvider>());
        using var scope = serviceProvider.CreateScope();

        var leaseProvider = scope.ServiceProvider.GetRequiredService<IConnectionLeaseProvider>();

        leaseProvider.Should().BeOfType<FirstLeaseProvider>();
        created.Should().BeEmpty("the generic overload replaces the factory registration with a container-constructed one");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UseConnectionLeaseProvider_InAnyOrderWithAddSqlServer_RegisteredProviderWins(bool addSqlServerFirst)
    {
        using var serviceProvider = BuildServiceProvider(o =>
        {
            if (addSqlServerFirst)
                o.AddSqlServer(Configuration).UseConnectionLeaseProvider<FirstLeaseProvider>();
            else
                o.UseConnectionLeaseProvider<FirstLeaseProvider>().AddSqlServer(Configuration);
        });
        using var scope = serviceProvider.CreateScope();

        var leaseProvider = scope.ServiceProvider.GetRequiredService<IConnectionLeaseProvider>();

        leaseProvider.Should().BeOfType<FirstLeaseProvider>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UseNServiceBusStorageSession_InAnyOrderWithAddSqlServer_ResolvesSqlStorageSessionConnectionLeaseProvider(bool addSqlServerFirst)
    {
        using var serviceProvider = BuildServiceProvider(o =>
        {
            if (addSqlServerFirst)
                o.AddSqlServer(Configuration).UseNServiceBusStorageSession();
            else
                o.UseNServiceBusStorageSession().AddSqlServer(Configuration);
        }, s => s.AddScoped(_ => Substitute.For<ISqlStorageSession>()));
        using var scope = serviceProvider.CreateScope();

        var leaseProviders = scope.ServiceProvider.GetServices<IConnectionLeaseProvider>();

        leaseProviders.Should().ContainSingle().Which.GetType().Name.Should().Be("SqlStorageSessionConnectionLeaseProvider");
    }

    [Fact]
    public void AddSqlServer_ReplacesDefaultUuidProvider_WithSingleTransientSqlServerUuidProvider()
    {
        var services = BuildServices(o => o.AddSqlServer(Configuration));

        var descriptor = services.Should().ContainSingle(s => s.ServiceType == typeof(IUuidProvider)).Subject;
        descriptor.ImplementationType.Should().Be(typeof(SqlServerUuidProvider));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Transient);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddSqlServer_InAnyOrderWithOtherOutboxConfiguration_RegistersSingleTransientSqlServerUuidProvider(bool addSqlServerFirst)
    {
        var services = BuildServices(o =>
        {
            if (addSqlServerFirst)
            {
                o.AddSqlServer(Configuration).UseNServiceBusStorageSession();
            }
            else
            {
                o.UseNServiceBusStorageSession().AddSqlServer(Configuration);
            }
        });

        var descriptor = services.Should().ContainSingle(s => s.ServiceType == typeof(IUuidProvider)).Subject;
        descriptor.ImplementationType.Should().Be(typeof(SqlServerUuidProvider));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Transient);
    }

    [Fact]
    public void AddOutbox_WithoutSqlServer_RegistersSingleTransientDefaultUuidProvider()
    {
        var services = BuildServices(o => { });

        var descriptor = services.Should().ContainSingle(s => s.ServiceType == typeof(IUuidProvider)).Subject;
        descriptor.ImplementationType.Should().NotBeNull();
        descriptor.ImplementationType!.Name.Should().Be("DefaultUuidProvider");
        descriptor.Lifetime.Should().Be(ServiceLifetime.Transient);
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

    private sealed class FirstLeaseProvider : IConnectionLeaseProvider
    {
        public ValueTask<ConnectionLease> Provide(CancellationToken cancellationToken)
            => throw new NotSupportedException("Never invoked by resolution-shape tests.");
    }

    private sealed class SecondLeaseProvider : IConnectionLeaseProvider
    {
        public ValueTask<ConnectionLease> Provide(CancellationToken cancellationToken)
            => throw new NotSupportedException("Never invoked by resolution-shape tests.");
    }
}
