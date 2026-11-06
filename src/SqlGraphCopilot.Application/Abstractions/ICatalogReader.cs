using SqlGraphCopilot.Application.Catalog;

namespace SqlGraphCopilot.Application.Abstractions;

/// <summary>Fuente de metadata. Reemplaza a los mocks: todo sale del catálogo real de SQL Server.</summary>
public interface ICatalogReader
{
    Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(string instance, CancellationToken cancellationToken);

    /// <summary>Objetos y dependencias de UNA base (sin el código fuente).</summary>
    Task<CatalogSnapshot> ReadCatalogAsync(string instance, string database, CancellationToken cancellationToken);

    /// <summary>Definición de un objeto (para clasificar sus dependencias). Null si está cifrado.</summary>
    Task<string?> ReadDefinitionAsync(string instance, string database, int objectId, CancellationToken cancellationToken);

    /// <summary>Todas las definiciones en streaming (escaneo profundo).</summary>
    IAsyncEnumerable<ObjectDefinition> ReadDefinitionsAsync(string instance, string database, CancellationToken cancellationToken);
}
