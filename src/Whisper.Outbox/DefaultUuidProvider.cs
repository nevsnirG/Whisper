using Whisper.Outbox.Abstractions;
using UUIDNext;

namespace Whisper.Outbox;

internal sealed class DefaultUuidProvider : IUuidProvider
{
    public Guid Provide()
    {
        return Uuid.NewDatabaseFriendly(Database.Other);
    }
}