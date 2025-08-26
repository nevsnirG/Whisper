using UUIDNext;

namespace Hermes.Outbox.MongoDb;
internal sealed class MongoDbUuidProvider : IUuidProvider
{
    public Guid Provide()
    {
        return Uuid.NewDatabaseFriendly(UUIDNext.Database.Other);
    }
}
