using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.SqlServer;
internal sealed class SqlOutboxInstaller(SqlOutboxConfiguration sqlOutboxConfiguration, IOptions<OutboxWorkerOptions> outboxWorkerOptions) : IInstallOutbox
{
    // Upgrading a large table scans it several times (CHECK validation and index builds), which can take far longer
    // than the default 30 s; a timeout there would fail startup on every restart.
    private const int InstallCommandTimeoutSeconds = 600;

    // The holder runs two batches of up to InstallCommandTimeoutSeconds each, so a waiter must be willing to wait
    // longer than both or it can give up just as the holder finishes.
    private const int InstallLockTimeoutMs = (2 * InstallCommandTimeoutSeconds + 60) * 1000;

    // sp_getapplock resource names are limited to 255 characters.
    private const int MaxLockResourceLength = 255;

    private const int MaxIdentifierLength = 128;

    public async Task InstallCollection(CancellationToken cancellationToken)
    {
        using var connection = new SqlConnection(sqlOutboxConfiguration.ConnectionString);
        var rawSchemaName = SqlIdentifier.Strip(sqlOutboxConfiguration.SchemaName);
        var rawTableName = SqlIdentifier.Strip(sqlOutboxConfiguration.TableName);
        var qualifiedSchema = SqlIdentifier.Bracket(sqlOutboxConfiguration.SchemaName);
        var qualifiedTable = $"{qualifiedSchema}.{SqlIdentifier.Bracket(sqlOutboxConfiguration.TableName)}";
        var lockResource = InstallLockResource(rawSchemaName, rawTableName);

        await connection.OpenAsync(cancellationToken);

        // Instances starting together against a fresh database would otherwise race on CREATE TABLE.
        // The later one waits here and then finds every object in place.
        await AcquireInstallLock(connection, lockResource, cancellationToken);
        try
        {
            // Two sequential batches on purpose: T-SQL resolves column names at batch compile time,
            // so the constraint/index batch would fail against a legacy-shape table before the
            // column-adding ALTERs ever ran.
            await ExecuteSchemaTableAndColumnsBatch(connection, qualifiedSchema, qualifiedTable, rawSchemaName, rawTableName, cancellationToken);
            await ExecuteConstraintsAndIndexesBatch(connection, qualifiedTable, rawTableName, cancellationToken);
        }
        finally
        {
            await ReleaseInstallLock(connection, lockResource);
        }
    }

    // A truncated name can only make two installs share a lock, which just runs them one after the other.
    private static string InstallLockResource(string rawSchemaName, string rawTableName)
    {
        var resource = $"Whisper.Outbox.Install:{rawSchemaName}.{rawTableName}";
        return resource.Length <= MaxLockResourceLength ? resource : resource[..MaxLockResourceLength];
    }

    // Constraint names are unique within a schema, not within a table, so each carries the table name: two outbox
    // tables can then share a schema. Tables created before the names did keep theirs (the base name alone), which
    // is why the existence checks below accept either.
    internal static string ConstraintName(string baseName, string rawTableName)
    {
        var name = $"{baseName}_{rawTableName}";
        if (name.Length <= MaxIdentifierLength)
            return name;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawTableName)))[..16];
        return $"{baseName}_{hash}";
    }

    private static async Task AcquireInstallLock(SqlConnection connection, string lockResource, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
    DECLARE @result INT;
    EXEC @result = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = @lockTimeoutMs;
    SELECT @result;";
        // The lock wait must end on @LockTimeout, not on the default 30 s command timeout.
        command.CommandTimeout = InstallLockTimeoutMs / 1000 + 30;
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, MaxLockResourceLength) { Value = lockResource });
        command.Parameters.Add(new SqlParameter("@lockTimeoutMs", SqlDbType.Int) { Value = InstallLockTimeoutMs });

        // 0 = granted, 1 = granted after waiting; negative = timeout (-1), cancelled (-2), deadlock victim (-3), error (-999).
        var result = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        if (result < 0)
            throw new InvalidOperationException(
                $"Could not acquire the outbox install lock '{lockResource}' within {InstallLockTimeoutMs} ms (sp_getapplock returned {result}).");
    }

    private static async Task ReleaseInstallLock(SqlConnection connection, string lockResource)
    {
        // A broken connection has already released its session-owned lock.
        if (connection.State != ConnectionState.Open)
            return;

        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';";
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, MaxLockResourceLength) { Value = lockResource });
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task ExecuteSchemaTableAndColumnsBatch(SqlConnection connection, string qualifiedSchema, string qualifiedTable, string rawSchemaName, string rawTableName, CancellationToken cancellationToken)
    {
        var literalSchema = SqlIdentifier.EscapeLiteral(qualifiedSchema);
        var literalTable = SqlIdentifier.EscapeLiteral(qualifiedTable);
        var primaryKey = SqlIdentifier.Bracket(ConstraintName("PK_OutboxRecords", rawTableName));
        var enqueuedAtDefault = SqlIdentifier.Bracket(ConstraintName("DF_Outbox_EnqueuedAtUtc", rawTableName));
        var retriesDefault = SqlIdentifier.Bracket(ConstraintName("DF_Outbox_Retries", rawTableName));

        await using var command = connection.CreateCommand();
        command.CommandText = $@"
    IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = @schema)
        EXEC(N'CREATE SCHEMA {literalSchema} AUTHORIZATION dbo');

    IF OBJECT_ID(N'{literalTable}', N'U') IS NULL
    BEGIN
        CREATE TABLE {qualifiedTable}
        (
            Id                    UNIQUEIDENTIFIER NOT NULL,
            EnqueuedAtUtc         DATETIMEOFFSET(7) NOT NULL
                CONSTRAINT {enqueuedAtDefault} DEFAULT (SYSUTCDATETIME()),
            DispatchedAtUtc       DATETIMEOFFSET(7) NULL,
            FailedAtUtc           DATETIMEOFFSET(7) NULL,
            Retries               INT NOT NULL CONSTRAINT {retriesDefault} DEFAULT (0),
            AssemblyQualifiedType NVARCHAR(2048) NOT NULL,
            Payload               NVARCHAR(MAX)   NOT NULL,
            LastError             NVARCHAR(MAX)   NULL,
            LastErrorAtUtc        DATETIMEOFFSET(7) NULL,
            NextRetryAtUtc        DATETIMEOFFSET(7) NULL,
            CONSTRAINT {primaryKey} PRIMARY KEY CLUSTERED (Id)
        );
    END;

    IF COL_LENGTH(N'{literalTable}', N'LastError') IS NULL
        ALTER TABLE {qualifiedTable} ADD LastError NVARCHAR(MAX) NULL;

    IF COL_LENGTH(N'{literalTable}', N'LastErrorAtUtc') IS NULL
        ALTER TABLE {qualifiedTable} ADD LastErrorAtUtc DATETIMEOFFSET(7) NULL;

    IF COL_LENGTH(N'{literalTable}', N'NextRetryAtUtc') IS NULL
        ALTER TABLE {qualifiedTable} ADD NextRetryAtUtc DATETIMEOFFSET(7) NULL;
";
        command.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = rawSchemaName });
        command.CommandTimeout = InstallCommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteConstraintsAndIndexesBatch(SqlConnection connection, string qualifiedTable, string rawTableName, CancellationToken cancellationToken)
    {
        var literalTable = SqlIdentifier.EscapeLiteral(qualifiedTable);

        string AddUtcCheck(string baseName, string column, bool nullable, bool validateExistingRows = true)
        {
            var name = ConstraintName(baseName, rawTableName);
            var expression = nullable
                ? $"{column} IS NULL OR DATEPART(TZ, {column}) = 0"
                : $"DATEPART(TZ, {column}) = 0";

            return $@"
    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name IN (N'{SqlIdentifier.EscapeLiteral(baseName)}', N'{SqlIdentifier.EscapeLiteral(name)}')
          AND parent_object_id = OBJECT_ID(N'{literalTable}'))
    BEGIN
        ALTER TABLE {qualifiedTable} WITH {(validateExistingRows ? "CHECK" : "NOCHECK")}
        ADD CONSTRAINT {SqlIdentifier.Bracket(name)}
        CHECK ({expression});
    END;
";
        }

        string IndexExists(string indexName) => $@"EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE name = N'{indexName}'
          AND object_id = OBJECT_ID(N'{literalTable}'))";

        var sql = new StringBuilder();
        sql.Append(AddUtcCheck("CK_Outbox_EnqueuedAt_IsUTC", "EnqueuedAtUtc", nullable: false));
        sql.Append(AddUtcCheck("CK_Outbox_DispatchedAt_IsUTC", "DispatchedAtUtc", nullable: true));
        // Not validated on purpose: FailedAtUtc predates this constraint, and an out-of-band legacy row
        // with a non-UTC offset would otherwise fail installation and gate the worker forever.
        sql.Append(AddUtcCheck("CK_Outbox_FailedAt_IsUTC", "FailedAtUtc", nullable: true, validateExistingRows: false));
        sql.Append(AddUtcCheck("CK_Outbox_LastErrorAt_IsUTC", "LastErrorAtUtc", nullable: true));
        sql.Append(AddUtcCheck("CK_Outbox_NextRetryAt_IsUTC", "NextRetryAtUtc", nullable: true));

        sql.Append($@"
    IF {IndexExists("IX_Outbox_Undispatched_ById")}
    BEGIN
        DROP INDEX IX_Outbox_Undispatched_ById ON {qualifiedTable};
    END;

    -- The ready index must carry its filter columns. Without them SQL Server plans a lookup per row to re-check
    -- the IS NULL filter, estimates that at a third of the table, and reads the whole table on every poll instead.
    -- An index created before it carried them is rebuilt.
    IF {IndexExists("IX_Outbox_Ready_ByDue")}
       AND NOT EXISTS (
        SELECT 1
        FROM sys.indexes i
        INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.name = N'IX_Outbox_Ready_ByDue'
          AND i.object_id = OBJECT_ID(N'{literalTable}')
          AND ic.is_included_column = 1
          AND c.name = N'DispatchedAtUtc')
    BEGIN
        DROP INDEX IX_Outbox_Ready_ByDue ON {qualifiedTable};
    END;

    IF NOT {IndexExists("IX_Outbox_Ready_ByDue")}
    BEGIN
        CREATE NONCLUSTERED INDEX IX_Outbox_Ready_ByDue
        ON {qualifiedTable} (NextRetryAtUtc, Id)
        INCLUDE (DispatchedAtUtc, FailedAtUtc)
        WHERE DispatchedAtUtc IS NULL AND FailedAtUtc IS NULL;
    END;

    IF NOT {IndexExists("IX_Outbox_Failed_ByFailedAt")}
    BEGIN
        CREATE NONCLUSTERED INDEX IX_Outbox_Failed_ByFailedAt
        ON {qualifiedTable} (FailedAtUtc DESC, Id)
        WHERE FailedAtUtc IS NOT NULL;
    END;
");

        // Only with retention switched on: the index gets an entry for every dispatched record, which is wasted
        // work when nothing ever deletes them. Without it the cleanup reads the whole table to find what expired.
        if (outboxWorkerOptions.Value.RetainDispatchedFor is not null)
        {
            sql.Append($@"
    IF NOT {IndexExists("IX_Outbox_Dispatched_ByDispatchedAt")}
    BEGIN
        CREATE NONCLUSTERED INDEX IX_Outbox_Dispatched_ByDispatchedAt
        ON {qualifiedTable} (DispatchedAtUtc)
        WHERE DispatchedAtUtc IS NOT NULL;
    END;
");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.CommandTimeout = InstallCommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
