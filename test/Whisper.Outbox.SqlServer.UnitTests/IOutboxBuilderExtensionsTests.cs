using Microsoft.Extensions.DependencyInjection;
using Whisper.Abstractions;
using Whisper.Outbox;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.SqlServer.UnitTests;

public class IOutboxBuilderExtensionsTests
{
    [Fact]
    public void AddSqlServer_RegistersRequiredServices()
    {
        var services = new ServiceCollection() as IServiceCollection;
        services.AddOptions();
        services.AddWhisper(b => b.AddOutbox(o =>
        {
            o.AddSqlServer(new SqlOutboxConfiguration
            {
                ConnectionString = "Server=fake;Database=test;"
            });
        }));

        services.Should().Contain(s => s.ServiceType == typeof(IOutboxStore) && s.ImplementationType == typeof(SqlOutboxStore));
        services.Should().Contain(s => s.ServiceType == typeof(IInstallOutbox) && s.ImplementationType == typeof(SqlOutboxInstaller));
        services.Should().Contain(s => s.ServiceType == typeof(IUuidProvider) && s.ImplementationType == typeof(SqlServerUuidProvider));
        services.Should().Contain(s => s.ServiceType == typeof(IConnectionLeaseProvider) && s.ImplementationType == typeof(SqlOutboxConfigurationConnectionLeaseProvider));
        services.Should().Contain(s => s.ServiceType == typeof(SqlOutboxConfiguration));
    }
}
