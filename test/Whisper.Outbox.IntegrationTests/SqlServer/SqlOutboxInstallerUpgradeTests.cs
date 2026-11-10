using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.IntegrationTests.Fakes;
using Whisper.Outbox.SqlServer;

namespace Whisper.Outbox.IntegrationTests.SqlServer;

/// <summary>
/// Upgrade path: a database created by the pre-recoverability installer (no error/retry columns,
/// legacy IX_Outbox_Undispatched_ById index) must be migrated in place, idempotently, without
/// touching existing rows.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class SqlOutboxInstallerUpgradeTests(MsSqlFixture sqlFixture)
{
    [Fact]
    public async Task InstallCollection_OnLegacySchema_AddsColumnsAndSwapsIndexes()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        var legacyRecordId = Guid.NewGuid();
        await CreateLegacyTableWithRecord(connectionString, legacyRecordId);
        await using var provider = BuildServiceProvider(connectionString);
        var installer = provider.GetRequiredService<IInstallOutbox>();

        await installer.InstallCollection(CancellationToken.None);

        (await ColumnExists(connectionString, "LastError")).Should().BeTrue();
        (await ColumnExists(connectionString, "LastErrorAtUtc")).Should().BeTrue();
        (await ColumnExists(connectionString, "NextRetryAtUtc")).Should().BeTrue();
        (await IndexExists(connectionString, "IX_Outbox_Undispatched_ById")).Should().BeFalse("the legacy index must be dropped");
        (await IndexExists(connectionString, "IX_Outbox_Ready_ByDue")).Should().BeTrue();
        (await IndexExists(connectionString, "IX_Outbox_Failed_ByFailedAt")).Should().BeTrue();
    }

    [Fact]
    public async Task InstallCollection_OnLegacySchema_SecondRunSucceeds()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        await CreateLegacyTableWithRecord(connectionString, Guid.NewGuid());
        await using var provider = BuildServiceProvider(connectionString);
        var installer = provider.GetRequiredService<IInstallOutbox>();
        await installer.InstallCollection(CancellationToken.None);

        var act = () => installer.InstallCollection(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task InstallCollection_OnLegacySchema_ExistingRowSurvivesAndIsImmediatelyEligible()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        var legacyRecordId = Guid.NewGuid();
        await CreateLegacyTableWithRecord(connectionString, legacyRecordId);
        await using var provider = BuildServiceProvider(connectionString);
        await provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);

        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var batch = await store.ReadNextBatch(10, DateTimeOffset.UtcNow, CancellationToken.None);

        // NextRetryAtUtc is NULL for pre-upgrade rows: eligible now, no backfill required.
        batch.Should().ContainSingle()
            .Which.Id.Should().Be(legacyRecordId);
    }

    // A table from before constraint names carried the table name keeps its constraints under their old names.
    // The installer must recognize them instead of adding a second copy under the new name.
    [Fact]
    public async Task InstallCollection_OnLegacySchema_DoesNotDuplicateExistingCheckConstraints()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        await CreateLegacyTableWithRecord(connectionString, Guid.NewGuid());
        await using var provider = BuildServiceProvider(connectionString);

        await provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);

        (await CheckConstraintNames(connectionString)).Should().BeEquivalentTo(
            "CK_Outbox_EnqueuedAt_IsUTC",
            "CK_Outbox_DispatchedAt_IsUTC",
            "CK_Outbox_FailedAt_IsUTC_outboxrecords",
            "CK_Outbox_LastErrorAt_IsUTC_outboxrecords",
            "CK_Outbox_NextRetryAt_IsUTC_outboxrecords");
    }

    // The one table per schema that older versions could create holds the unsuffixed constraint names.
    [Fact]
    public async Task InstallCollection_SecondTableNextToALegacyTable_Installs()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        await CreateLegacyTableWithRecord(connectionString, Guid.NewGuid());
        await using var provider = BuildServiceProvider(connectionString, tableName: "second_outbox");

        var act = () => provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);

        await act.Should().NotThrowAsync();
        (await SqlIndexes.Exists(connectionString, "second_outbox", "IX_Outbox_Ready_ByDue")).Should().BeTrue();
    }

    // The ready index as Whisper.Outbox.SqlServer 4.1 created it: without its filter columns, which made SQL Server
    // read the whole table on every poll.
    [Fact]
    public async Task InstallCollection_ReadyIndexWithoutItsFilterColumns_IsRebuiltWithThem()
    {
        var connectionString = await sqlFixture.CreateDatabaseAsync();
        await using var provider = BuildServiceProvider(connectionString);
        var installer = provider.GetRequiredService<IInstallOutbox>();
        await installer.InstallCollection(CancellationToken.None);
        await Execute(connectionString, """
DROP INDEX IX_Outbox_Ready_ByDue ON [dbo].[outboxrecords];
CREATE NONCLUSTERED INDEX IX_Outbox_Ready_ByDue
ON [dbo].[outboxrecords] (NextRetryAtUtc, Id)
WHERE DispatchedAtUtc IS NULL AND FailedAtUtc IS NULL;
""");
        (await SqlIndexes.IncludedColumns(connectionString, "outboxrecords", "IX_Outbox_Ready_ByDue")).Should().BeEmpty();

        await installer.InstallCollection(CancellationToken.None);

        (await SqlIndexes.IncludedColumns(connectionString, "outboxrecords", "IX_Outbox_Ready_ByDue"))
            .Should().BeEquivalentTo("DispatchedAtUtc", "FailedAtUtc");
    }

    private static async Task<string[]> CheckConstraintNames(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'[dbo].[outboxrecords]')";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return [.. names];
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static ServiceProvider BuildServiceProvider(string connectionString, string? tableName = null)
    {
        var configuration = tableName is null
            ? new SqlOutboxConfiguration { ConnectionString = connectionString }
            : new SqlOutboxConfiguration { ConnectionString = connectionString, TableName = tableName };

        return BuildServiceProvider(configuration);
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

    // The exact table shape the previous installer (Whisper.Outbox.SqlServer 3.x) created.
    private static async Task CreateLegacyTableWithRecord(string connectionString, Guid recordId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
CREATE TABLE [dbo].[outboxrecords]
(
    Id                    UNIQUEIDENTIFIER NOT NULL,
    EnqueuedAtUtc         DATETIMEOFFSET(7) NOT NULL
        CONSTRAINT DF_Outbox_EnqueuedAtUtc DEFAULT (SYSUTCDATETIME()),
    DispatchedAtUtc       DATETIMEOFFSET(7) NULL,
    FailedAtUtc           DATETIMEOFFSET(7) NULL,
    Retries               INT NOT NULL CONSTRAINT DF_Outbox_Retries DEFAULT (0),
    AssemblyQualifiedType NVARCHAR(2048) NOT NULL,
    Payload               NVARCHAR(MAX)   NOT NULL,
    CONSTRAINT PK_OutboxRecords PRIMARY KEY CLUSTERED (Id)
);

ALTER TABLE [dbo].[outboxrecords] WITH CHECK
ADD CONSTRAINT CK_Outbox_EnqueuedAt_IsUTC CHECK (DATEPART(TZ, EnqueuedAtUtc) = 0);

ALTER TABLE [dbo].[outboxrecords] WITH CHECK
ADD CONSTRAINT CK_Outbox_DispatchedAt_IsUTC CHECK (DispatchedAtUtc IS NULL OR DATEPART(TZ, DispatchedAtUtc) = 0);

CREATE NONCLUSTERED INDEX IX_Outbox_Undispatched_ById
ON [dbo].[outboxrecords] (DispatchedAtUtc, FailedAtUtc, Id)
WHERE DispatchedAtUtc IS NULL AND FailedAtUtc IS NULL;

INSERT INTO [dbo].[outboxrecords] (Id, EnqueuedAtUtc, AssemblyQualifiedType, Payload)
VALUES (@id, SYSUTCDATETIME(), @type, N'{"Value":"legacy"}');
""";
        command.Parameters.AddWithValue("@id", recordId);
        command.Parameters.AddWithValue("@type", typeof(TestDomainEvent).AssemblyQualifiedName!);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ColumnExists(string connectionString, string columnName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COL_LENGTH(N'[dbo].[outboxrecords]', N'{columnName}')";
        var result = await command.ExecuteScalarAsync();
        return result is not (null or DBNull);
    }

    private static async Task<bool> IndexExists(string connectionString, string indexName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COUNT(*) FROM sys.indexes
WHERE name = @index AND object_id = OBJECT_ID(N'[dbo].[outboxrecords]')";
        command.Parameters.AddWithValue("@index", indexName);
        return (int)(await command.ExecuteScalarAsync())! == 1;
    }
}
