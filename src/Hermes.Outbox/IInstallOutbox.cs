namespace Hermes.Outbox;
public interface IInstallOutbox
{
    Task InstallCollection(CancellationToken cancellationToken);
}