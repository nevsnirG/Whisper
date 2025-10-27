using Microsoft.Data.SqlClient;
using System.Data;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.SqlServer;
internal sealed class SqlOutboxInstaller(SqlOutboxConfiguration sqlOutboxConfiguration) : IInstallOutbox
{
    public async Task InstallCollection(CancellationToken cancellationToken)
    {
        using var connection = new SqlConnection(sqlOutboxConfiguration.ConnectionString);
        var qualifiedSchema = Clean(sqlOutboxConfiguration.SchemaName);
        var qualifiedTable = QualifyTableName(qualifiedSchema, sqlOutboxConfiguration.TableName);
        await using var command = connection.CreateCommand();
        command.CommandText = $@"
    IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = @schema)
        EXEC(N'CREATE SCHEMA {qualifiedSchema} AUTHORIZATION dbo');

    IF OBJECT_ID(N'{qualifiedTable}', N'U') IS NULL
    BEGIN
        CREATE TABLE {qualifiedTable}
        (
            Id                    UNIQUEIDENTIFIER NOT NULL,
            EnqueuedAtUtc         DATETIMEOFFSET(7) NOT NULL 
                CONSTRAINT DF_Outbox_EnqueuedAtUtc DEFAULT (SYSUTCDATETIME()),
            DispatchedAtUtc       DATETIMEOFFSET(7) NULL,
            AssemblyQualifiedType NVARCHAR(2048) NOT NULL,
            Payload               NVARCHAR(MAX)   NOT NULL,
            CONSTRAINT PK_OutboxRecords PRIMARY KEY CLUSTERED (Id)
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints 
        WHERE name = N'CK_Outbox_EnqueuedAt_IsUTC' 
          AND parent_object_id = OBJECT_ID(N'{qualifiedTable}'))
    BEGIN
        ALTER TABLE {qualifiedTable} WITH CHECK
        ADD CONSTRAINT CK_Outbox_EnqueuedAt_IsUTC
        CHECK (DATEPART(TZ, EnqueuedAtUtc) = 0);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints 
        WHERE name = N'CK_Outbox_DispatchedAt_IsUTC' 
          AND parent_object_id = OBJECT_ID(N'{qualifiedTable}'))
    BEGIN
        ALTER TABLE {qualifiedTable} WITH CHECK
        ADD CONSTRAINT CK_Outbox_DispatchedAt_IsUTC
        CHECK (DispatchedAtUtc IS NULL OR DATEPART(TZ, DispatchedAtUtc) = 0);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes 
        WHERE name = N'IX_Outbox_Undispatched_ById' 
          AND object_id = OBJECT_ID(N'{qualifiedTable}'))
    BEGIN
        CREATE NONCLUSTERED INDEX IX_Outbox_Undispatched_ById
        ON {qualifiedTable} (DispatchedAtUtc, Id)
        WHERE DispatchedAtUtc IS NULL;
    END;
";
        command.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = qualifiedSchema });

        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string QualifyTableName(string schema, string table)
    {
        return $"{Clean(schema)}.{Clean(table)}";
    }

    private static string Clean(string identifier)
        => $"[{identifier.Replace("[", string.Empty).Replace("]", string.Empty)}]";
}
