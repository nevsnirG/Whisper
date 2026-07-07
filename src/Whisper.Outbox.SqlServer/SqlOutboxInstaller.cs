using Microsoft.Data.SqlClient;
using System.Data;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.SqlServer;
internal sealed class SqlOutboxInstaller(SqlOutboxConfiguration sqlOutboxConfiguration) : IInstallOutbox
{
    public async Task InstallCollection(CancellationToken cancellationToken)
    {
        using var connection = new SqlConnection(sqlOutboxConfiguration.ConnectionString);
        var rawSchemaName = SqlIdentifier.Strip(sqlOutboxConfiguration.SchemaName);
        var qualifiedSchema = SqlIdentifier.Bracket(sqlOutboxConfiguration.SchemaName);
        var qualifiedTable = $"{qualifiedSchema}.{SqlIdentifier.Bracket(sqlOutboxConfiguration.TableName)}";

        await connection.OpenAsync(cancellationToken);

        // Two sequential batches on purpose: T-SQL resolves column names at batch compile time,
        // so the constraint/index batch would fail against a legacy-shape table before the
        // column-adding ALTERs ever ran.
        await ExecuteSchemaTableAndColumnsBatch(connection, qualifiedSchema, qualifiedTable, rawSchemaName, cancellationToken);
        await ExecuteConstraintsAndIndexesBatch(connection, qualifiedTable, cancellationToken);
    }

    private static async Task ExecuteSchemaTableAndColumnsBatch(SqlConnection connection, string qualifiedSchema, string qualifiedTable, string rawSchemaName, CancellationToken cancellationToken)
    {
        var literalSchema = SqlIdentifier.EscapeLiteral(qualifiedSchema);
        var literalTable = SqlIdentifier.EscapeLiteral(qualifiedTable);

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
                CONSTRAINT DF_Outbox_EnqueuedAtUtc DEFAULT (SYSUTCDATETIME()),
            DispatchedAtUtc       DATETIMEOFFSET(7) NULL,
            FailedAtUtc           DATETIMEOFFSET(7) NULL,
            Retries               INT NOT NULL CONSTRAINT DF_Outbox_Retries DEFAULT (0),
            AssemblyQualifiedType NVARCHAR(2048) NOT NULL,
            Payload               NVARCHAR(MAX)   NOT NULL,
            LastError             NVARCHAR(MAX)   NULL,
            LastErrorAtUtc        DATETIMEOFFSET(7) NULL,
            NextRetryAtUtc        DATETIMEOFFSET(7) NULL,
            CONSTRAINT PK_OutboxRecords PRIMARY KEY CLUSTERED (Id)
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
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteConstraintsAndIndexesBatch(SqlConnection connection, string qualifiedTable, CancellationToken cancellationToken)
    {
        var literalTable = SqlIdentifier.EscapeLiteral(qualifiedTable);

        await using var command = connection.CreateCommand();
        command.CommandText = $@"
    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_Outbox_EnqueuedAt_IsUTC'
          AND parent_object_id = OBJECT_ID(N'{literalTable}'))
    BEGIN
        ALTER TABLE {qualifiedTable} WITH CHECK
        ADD CONSTRAINT CK_Outbox_EnqueuedAt_IsUTC
        CHECK (DATEPART(TZ, EnqueuedAtUtc) = 0);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_Outbox_DispatchedAt_IsUTC'
          AND parent_object_id = OBJECT_ID(N'{literalTable}'))
    BEGIN
        ALTER TABLE {qualifiedTable} WITH CHECK
        ADD CONSTRAINT CK_Outbox_DispatchedAt_IsUTC
        CHECK (DispatchedAtUtc IS NULL OR DATEPART(TZ, DispatchedAtUtc) = 0);
    END;

    -- NOCHECK on purpose: FailedAtUtc predates this constraint, and an out-of-band legacy row
    -- with a non-UTC offset would otherwise fail installation and gate the worker forever.
    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_Outbox_FailedAt_IsUTC'
          AND parent_object_id = OBJECT_ID(N'{literalTable}'))
    BEGIN
        ALTER TABLE {qualifiedTable} WITH NOCHECK
        ADD CONSTRAINT CK_Outbox_FailedAt_IsUTC
        CHECK (FailedAtUtc IS NULL OR DATEPART(TZ, FailedAtUtc) = 0);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_Outbox_LastErrorAt_IsUTC'
          AND parent_object_id = OBJECT_ID(N'{literalTable}'))
    BEGIN
        ALTER TABLE {qualifiedTable} WITH CHECK
        ADD CONSTRAINT CK_Outbox_LastErrorAt_IsUTC
        CHECK (LastErrorAtUtc IS NULL OR DATEPART(TZ, LastErrorAtUtc) = 0);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_Outbox_NextRetryAt_IsUTC'
          AND parent_object_id = OBJECT_ID(N'{literalTable}'))
    BEGIN
        ALTER TABLE {qualifiedTable} WITH CHECK
        ADD CONSTRAINT CK_Outbox_NextRetryAt_IsUTC
        CHECK (NextRetryAtUtc IS NULL OR DATEPART(TZ, NextRetryAtUtc) = 0);
    END;

    IF EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE name = N'IX_Outbox_Undispatched_ById'
          AND object_id = OBJECT_ID(N'{literalTable}'))
    BEGIN
        DROP INDEX IX_Outbox_Undispatched_ById ON {qualifiedTable};
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE name = N'IX_Outbox_Ready_ByDue'
          AND object_id = OBJECT_ID(N'{literalTable}'))
    BEGIN
        CREATE NONCLUSTERED INDEX IX_Outbox_Ready_ByDue
        ON {qualifiedTable} (NextRetryAtUtc, Id)
        WHERE DispatchedAtUtc IS NULL AND FailedAtUtc IS NULL;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE name = N'IX_Outbox_Failed_ByFailedAt'
          AND object_id = OBJECT_ID(N'{literalTable}'))
    BEGIN
        CREATE NONCLUSTERED INDEX IX_Outbox_Failed_ByFailedAt
        ON {qualifiedTable} (FailedAtUtc DESC, Id)
        WHERE FailedAtUtc IS NOT NULL;
    END;
";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
