using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Whisper.Outbox.IntegrationTests.SqlServer;

public sealed class MsSqlFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task<string> CreateDatabaseAsync()
    {
        var databaseName = $"whisper_test_{Guid.NewGuid():N}";

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{databaseName}]";
        await command.ExecuteNonQueryAsync();

        return new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = databaseName,
        }.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
