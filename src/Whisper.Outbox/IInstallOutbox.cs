namespace Whisper.Outbox;
public interface IInstallOutbox
{
    Task InstallCollection(CancellationToken cancellationToken);
}