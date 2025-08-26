using Microsoft.Data.SqlClient;

namespace Hermes.Outbox.SqlServer;
public interface IConnectionLease : IAsyncDisposable
{
    SqlConnection Connection { get; }

    SqlTransaction? Transaction { get; }
}
