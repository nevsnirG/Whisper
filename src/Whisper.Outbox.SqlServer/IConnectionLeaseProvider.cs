namespace Whisper.Outbox.SqlServer;

/// <summary>
/// Supplies the SQL connection (and optional transaction) of the unit of work that is active in the current scope,
/// so the events a use case raises are stored together with its own changes. The outbox store asks it when it
/// stores events and at no other time: the worker's reads and updates never go through it.
/// </summary>
public interface IConnectionLeaseProvider
{
    /// <summary>
    /// Returns the connection and transaction of the unit of work active in this scope, or null when there is none.
    /// With a lease, the outbox store uses it as it is and never opens, commits, rolls back or disposes it. With
    /// null, the store opens and disposes its own connection for the write, as if no provider were registered.
    /// </summary>
    ValueTask<ConnectionLease?> Provide(CancellationToken cancellationToken);
}
