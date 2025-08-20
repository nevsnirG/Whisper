namespace Hermes.Outbox;
public interface IUuidProvider
{
    Guid Provide();
}