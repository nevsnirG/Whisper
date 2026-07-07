using Microsoft.Data.SqlClient;

namespace Whisper.Outbox.SqlServer;
public sealed record ConnectionLease(SqlConnection Connection, SqlTransaction? Transaction = null);
