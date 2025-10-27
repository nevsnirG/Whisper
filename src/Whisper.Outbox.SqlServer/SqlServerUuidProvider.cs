using Whisper.Outbox.Abstractions;
using UUIDNext;

namespace Whisper.Outbox.SqlServer;
internal sealed class SqlServerUuidProvider : IUuidProvider
{
    public Guid Provide()
    {
        return Uuid.NewDatabaseFriendly(Database.SqlServer);
    }
}