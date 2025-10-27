namespace Whisper.Outbox.SqlServer;
public sealed class SqlOutboxConfiguration
{
    public required string ConnectionString { get; init; }
    public string TableName { get; init; } = "outboxrecords";
    public string SchemaName { get; init; } = "dbo";
}