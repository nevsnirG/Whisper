namespace Whisper.Outbox.Abstractions;
public interface IInstallOutbox
{
    Task InstallCollection(CancellationToken cancellationToken);
}