using UUIDNext;

namespace Hermes.Outbox;
public interface IUuidProvider
{
    Guid Provide();
}

internal sealed class DefaultUuidProvider : IUuidProvider
{
    public Guid Provide()
    {
        return Uuid.NewDatabaseFriendly(Database.Other);
    }
}