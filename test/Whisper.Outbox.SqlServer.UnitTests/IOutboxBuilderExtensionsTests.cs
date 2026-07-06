using Microsoft.Extensions.DependencyInjection;
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

        services.Should().Contain(s => s.ServiceType == typeof(IOutboxStore) && s.ImplementationType == typeof(SqlOutboxStore));
        services.Should().Contain(s => s.ServiceType == typeof(IInstallOutbox) && s.ImplementationType == typeof(SqlOutboxInstaller));
        services.Should().Contain(s => s.ServiceType == typeof(IUuidProvider) && s.ImplementationType == typeof(SqlServerUuidProvider));
        services.Should().Contain(s => s.ServiceType == typeof(IConnectionLeaseProvider) && s.ImplementationType == typeof(SqlOutboxConfigurationConnectionLeaseProvider));
        services.Should().Contain(s => s.ServiceType == typeof(SqlOutboxConfiguration));
    }

    [Fact]
    public void AddSqlServer_WithoutNServiceBusStorageSession_RegistersSingleTransientSqlOutboxConfigurationConnectionLeaseProvider()
    {
        var services = BuildServices(o => o.AddSqlServer(Configuration));

        var descriptor = services.Should().ContainSingle(s => s.ServiceType == typeof(IConnectionLeaseProvider)).Subject;
        descriptor.ImplementationType.Should().Be(typeof(SqlOutboxConfigurationConnectionLeaseProvider));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Transient);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UseNServiceBusStorageSession_InAnyOrderWithAddSqlServer_RegistersSingleScopedSqlStorageSessionConnectionLeaseProvider(bool addSqlServerFirst)
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

        var descriptor = services.Should().ContainSingle(s => s.ServiceType == typeof(IConnectionLeaseProvider)).Subject;
        descriptor.ImplementationType.Should().NotBeNull();
        descriptor.ImplementationType!.Name.Should().Be("SqlStorageSessionConnectionLeaseProvider");
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
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

    private static IServiceCollection BuildServices(Action<IOutboxBuilder> configure)
    {
        var services = new ServiceCollection() as IServiceCollection;
        services.AddOptions();
        services.AddWhisper(b => b.AddOutbox(configure));
        return services;
    }
}
