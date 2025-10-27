namespace Whisper.Outbox.Abstractions;
public interface IUuidProvider
{
    Guid Provide();
}
