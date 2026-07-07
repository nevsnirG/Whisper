using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.SqlServer;

namespace Whisper.Outbox.IntegrationTests.SqlServer;

[Collection(MsSqlCollection.Name)]
public sealed class SqlOutboxInstallerTests(MsSqlFixture sqlFixture)
{
    // Regression guard: the schema existence check must compare against the raw schema name,
    // not the bracketed identifier, or CREATE SCHEMA runs (and throws) on every run after the first.
    [Theory]
    [InlineData("whisper", "whisper")]
    [InlineData("[whisper2]", "whisper2")]
    public async Task InstallCollection_NonDefaultSchema_SecondRunSucceeds(string configuredSchemaName, string expectedSchemaName)
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        var configuration = new SqlOutboxConfiguration
        {
            ConnectionString = connectionString,
            SchemaName = configuredSchemaName,
        };
        await using var provider = BuildServiceProvider(configuration);
        var installer = provider.GetRequiredService<IInstallOutbox>();
        await installer.InstallCollection(CancellationToken.None);

        var act = () => installer.InstallCollection(CancellationToken.None);

        await act.Should().NotThrowAsync();
        (await SchemaExists(connectionString, expectedSchemaName)).Should().BeTrue();
        (await TableExists(connectionString, expectedSchemaName, configuration.TableName)).Should().BeTrue();
    }

    private static ServiceProvider BuildServiceProvider(SqlOutboxConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(Whispers).Assembly);
        });
        services.AddWhisper(b =>
        {
            b.AddMediatR();
            b.AddOutbox(o => o.AddSqlServer(configuration));
        });
        return services.BuildServiceProvider();
    }

    private static async Task<bool> SchemaExists(string connectionString, string schemaName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.schemas WHERE name = @schema";
        command.Parameters.AddWithValue("@schema", schemaName);
        return (int)(await command.ExecuteScalarAsync())! == 1;
    }

    private static async Task<bool> TableExists(string connectionString, string schemaName, string tableName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COUNT(*)
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = @schema AND t.name = @table";
        command.Parameters.AddWithValue("@schema", schemaName);
        command.Parameters.AddWithValue("@table", tableName);
        return (int)(await command.ExecuteScalarAsync())! == 1;
    }
}
