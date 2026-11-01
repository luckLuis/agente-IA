using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace SqlGraphCopilot.Core.Metadata;

public interface IDbConnectionFactory
{
    /// <summary>Crea (sin abrir) una conexión a la instancia registrada, opcionalmente a una base concreta.</summary>
    DbConnection Create(string instanceName, string? database);
}

/// <summary>
/// Único punto del Core que depende de Microsoft.Data.SqlClient.
/// La base se fija con InitialCatalog (no se concatena en SQL): no hay inyección por nombre de base.
/// </summary>
public sealed class SqlServerConnectionFactory : IDbConnectionFactory
{
    private readonly IInstanceRegistry _registry;

    public SqlServerConnectionFactory(IInstanceRegistry registry)
    {
        _registry = registry;
    }

    public DbConnection Create(string instanceName, string? database)
    {
        SqlInstanceOptions instance = _registry.GetRequired(instanceName);
        SqlConnectionStringBuilder builder = new(instance.ConnectionString)
        {
            ApplicationName = "SqlGraphCopilot",
            ApplicationIntent = ApplicationIntent.ReadOnly
        };

        if (!string.IsNullOrWhiteSpace(database))
        {
            builder.InitialCatalog = database;
        }

        return new SqlConnection(builder.ConnectionString);
    }
}
