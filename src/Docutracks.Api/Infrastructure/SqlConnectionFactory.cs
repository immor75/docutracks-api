using System.Data.Common;

namespace Docutracks.Api.Infrastructure;

/// <summary>The only place that names the SQL Server client.</summary>
public sealed class SqlConnectionFactory(IConfiguration config) : IDbConnectionFactory
{
    private readonly string connectionString = config.GetConnectionString("Docutracks")
        ?? throw new InvalidOperationException("Λείπει το ConnectionStrings:Docutracks στις ρυθμίσεις.");

#if OFFLINE
    // Built without Microsoft.Data.SqlClient (no access to nuget.org): the service starts, SQL fails.
    public DbConnection Create() =>
        throw new InvalidOperationException("Built with -p:Offline=true: no SQL Server client. Build normally to read the database.");
#else
    public DbConnection Create() => new Microsoft.Data.SqlClient.SqlConnection(connectionString);
#endif
}
