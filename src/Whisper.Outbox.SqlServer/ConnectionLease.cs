using Microsoft.Data.SqlClient;

namespace Whisper.Outbox.SqlServer;
public interface IConnectionLease : IAsyncDisposable
{
    SqlConnection Connection { get; }

    SqlTransaction? Transaction { get; }
}
