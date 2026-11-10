using Microsoft.Data.SqlClient;

namespace Whisper.Outbox.IntegrationTests.SqlServer;

/// <summary>Catalog and usage queries about the indexes of an outbox table in the dbo schema.</summary>
internal static class SqlIndexes
{
    public static async Task<bool> Exists(string connectionString, string tableName, string indexName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE name = @index AND object_id = OBJECT_ID(@table)";
        command.Parameters.AddWithValue("@index", indexName);
        command.Parameters.AddWithValue("@table", $"[dbo].[{tableName}]");
        return (int)(await command.ExecuteScalarAsync())! == 1;
    }

    public static async Task<string[]> IncludedColumns(string connectionString, string tableName, string indexName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT c.name
FROM sys.indexes i
INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.name = @index AND i.object_id = OBJECT_ID(@table) AND ic.is_included_column = 1";
        command.Parameters.AddWithValue("@index", indexName);
        command.Parameters.AddWithValue("@table", $"[dbo].[{tableName}]");

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));
        return [.. columns];
    }

    /// <summary>
    /// How often queries have read each index of the table so far: seeks and scans per index name, with the
    /// clustered index (the table itself) under <see cref="Table"/>. Compare two snapshots to see what one
    /// operation read.
    /// </summary>
    public static async Task<Dictionary<string, IndexReads>> Reads(string connectionString, string tableName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT CASE WHEN i.index_id = 1 THEN @clustered ELSE i.name END, ISNULL(s.user_seeks, 0), ISNULL(s.user_scans, 0)
FROM sys.indexes i
LEFT JOIN sys.dm_db_index_usage_stats s
    ON s.database_id = DB_ID() AND s.object_id = i.object_id AND s.index_id = i.index_id
WHERE i.object_id = OBJECT_ID(@table)";
        command.Parameters.AddWithValue("@clustered", Table);
        command.Parameters.AddWithValue("@table", $"[dbo].[{tableName}]");

        var reads = new Dictionary<string, IndexReads>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            reads[reader.GetString(0)] = new IndexReads(reader.GetInt64(1), reader.GetInt64(2));
        return reads;
    }

    public const string Table = "(the table)";

    public sealed record IndexReads(long Seeks, long Scans)
    {
        public IndexReads Since(IndexReads earlier) => new(Seeks - earlier.Seeks, Scans - earlier.Scans);
    }
}
