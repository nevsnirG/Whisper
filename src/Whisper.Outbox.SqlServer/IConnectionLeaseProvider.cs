namespace Whisper.Outbox.SqlServer;
public interface IConnectionLeaseProvider
{
    ValueTask<IConnectionLease> Provide(CancellationToken cancellationToken);
}