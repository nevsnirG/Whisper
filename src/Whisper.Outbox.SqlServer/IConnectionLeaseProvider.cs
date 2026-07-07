namespace Whisper.Outbox.SqlServer;
public interface IConnectionLeaseProvider
{
    ValueTask<ConnectionLease> Provide(CancellationToken cancellationToken);
}
