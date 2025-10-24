using Hermes.Outbox.Abstractions;
using UUIDNext;

namespace Hermes.Outbox;

internal sealed class DefaultUuidProvider : IUuidProvider
{
    public Guid Provide()
    {
        return Uuid.NewDatabaseFriendly(Database.Other);
    }
}