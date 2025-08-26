using UUIDNext;

namespace Hermes.Outbox.SqlServer;
internal sealed class SqlServerUuidProvider : IUuidProvider
{
    public Guid Provide()
    {
        return Uuid.NewDatabaseFriendly(Database.SqlServer);
    }
}