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

    // Several instances starting together against a fresh database: without the install lock they race
    // on CREATE TABLE and all but one fail with "There is already an object named ...".
    [Fact]
    public async Task InstallCollection_ConcurrentRunsOnFreshDatabase_AllSucceed()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        var configuration = new SqlOutboxConfiguration { ConnectionString = connectionString };
        var providers = Enumerable.Range(0, 4).Select(_ => BuildServiceProvider(configuration)).ToArray();
        try
        {
            var act = () => Task.WhenAll(providers.Select(p =>
                Task.Run(() => p.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None))));

            await act.Should().NotThrowAsync();
            (await TableExists(connectionString, configuration.SchemaName, configuration.TableName)).Should().BeTrue();
        }
        finally
        {
            foreach (var provider in providers)
                await provider.DisposeAsync();
        }
    }

    // Constraint names are unique within a schema, not within a table: with fixed names the second table's
    // CREATE TABLE fails with "There is already an object named 'PK_OutboxRecords' in the database".
    [Fact]
    public async Task InstallCollection_TwoTablesInOneSchema_BothInstall()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();

        foreach (var tableName in new[] { "orders_outbox", "billing_outbox" })
        {
            var configuration = new SqlOutboxConfiguration { ConnectionString = connectionString, TableName = tableName };
            await using var provider = BuildServiceProvider(configuration);
            // Twice: the second run must find the first run's constraints under their table-specific names.
            await provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);
            await provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);
        }

        (await TableExists(connectionString, "dbo", "orders_outbox")).Should().BeTrue();
        (await TableExists(connectionString, "dbo", "billing_outbox")).Should().BeTrue();
        (await CountCheckConstraints(connectionString, "orders_outbox")).Should().Be(5);
        (await CountCheckConstraints(connectionString, "billing_outbox")).Should().Be(5);
    }

    // Without its filter columns the ready index is ignored once the table holds data, and every poll reads the
    // whole table instead (see SqlOutboxIndexUsageTests).
    [Fact]
    public async Task InstallCollection_ReadyIndex_CarriesItsFilterColumns()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        await using var provider = BuildServiceProvider(new SqlOutboxConfiguration { ConnectionString = connectionString });

        await provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);

        (await SqlIndexes.IncludedColumns(connectionString, "outboxrecords", "IX_Outbox_Ready_ByDue"))
            .Should().BeEquivalentTo("DispatchedAtUtc", "FailedAtUtc");
    }

    // The index gets an entry for every dispatched record, so it is only worth having when something deletes them.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallCollection_CreatesTheDispatchedIndex_OnlyWhenRetentionIsConfigured(bool retentionConfigured)
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        await using var provider = BuildServiceProvider(
            new SqlOutboxConfiguration { ConnectionString = connectionString },
            retainDispatchedFor: retentionConfigured ? TimeSpan.FromDays(7) : null);

        await provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);

        (await SqlIndexes.Exists(connectionString, "outboxrecords", "IX_Outbox_Dispatched_ByDispatchedAt")).Should().Be(retentionConfigured);
    }

    private static ServiceProvider BuildServiceProvider(SqlOutboxConfiguration configuration, TimeSpan? retainDispatchedFor = null)
    {
        var services = new ServiceCollection();
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(Whispers).Assembly);
        });
        services.AddWhisper(b =>
        {
            b.AddMediatR();
            b.AddOutbox(o => o
                .ConfigureWorker(w => w.RetainDispatchedFor = retainDispatchedFor)
                .AddSqlServer(configuration));
        });
        return services.BuildServiceProvider();
    }

    private static async Task<int> CountCheckConstraints(string connectionString, string tableName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(@table)";
        command.Parameters.AddWithValue("@table", $"[dbo].[{tableName}]");
        return (int)(await command.ExecuteScalarAsync())!;
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
