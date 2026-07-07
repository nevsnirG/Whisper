namespace Whisper.Outbox.IntegrationTests.SqlServer;

[CollectionDefinition(Name)]
public sealed class MsSqlCollection : ICollectionFixture<MsSqlFixture>
{
    public const string Name = "SqlServer";
}
