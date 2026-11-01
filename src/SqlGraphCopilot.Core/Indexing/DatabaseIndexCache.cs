using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlGraphCopilot.Core.Metadata;

namespace SqlGraphCopilot.Core.Indexing;

public interface IDatabaseIndexProvider
{
    /// <summary>Devuelve el índice de la base (lo construye la primera vez; peticiones concurrentes comparten la misma construcción).</summary>
    Task<DatabaseIndex> GetAsync(string instance, string database, CancellationToken cancellationToken);

    /// <summary>Índices ya construidos (no dispara lecturas nuevas). Se usa para impacto cross-database.</summary>
    IReadOnlyList<DatabaseIndex> GetLoaded();

    void Invalidate(string instance, string database);
}

/// <summary>
/// Caché por (instancia, base) con expiración. Solo se lee la base que el usuario selecciona:
/// nunca hay lecturas globales de todas las instancias.
/// </summary>
public sealed class DatabaseIndexCache : IDatabaseIndexProvider
{
    private readonly ConcurrentDictionary<string, Lazy<Task<DatabaseIndex>>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICatalogReader _reader;
    private readonly DatabaseIndexBuilder _builder;
    private readonly IInstanceRegistry _registry;
    private readonly SqlGraphCopilotOptions _options;
    private readonly ILogger<DatabaseIndexCache> _logger;

    public DatabaseIndexCache(
        ICatalogReader reader,
        DatabaseIndexBuilder builder,
        IInstanceRegistry registry,
        IOptions<SqlGraphCopilotOptions> options,
        ILogger<DatabaseIndexCache> logger)
    {
        _reader = reader;
        _builder = builder;
        _registry = registry;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DatabaseIndex> GetAsync(string instance, string database, CancellationToken cancellationToken)
    {
        SqlInstanceOptions registered = _registry.GetRequired(instance);
        if (!_registry.IsDatabaseAllowed(registered.Name, database))
        {
            throw new DatabaseNotAccessibleException(registered.Name, database, "La base está excluida en la configuración.");
        }

        string key = CacheKey(registered.Name, database);
        TimeSpan ttl = TimeSpan.FromMinutes(Math.Max(1, _options.IndexCacheMinutes));

        for (int attempt = 0; attempt < 2; attempt++)
        {
            Lazy<Task<DatabaseIndex>> entry = _entries.GetOrAdd(
                key,
                _ => new Lazy<Task<DatabaseIndex>>(() => BuildAsync(registered.Name, database), LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                // La construcción no se cancela si un cliente cancela: otros pueden estar esperando el mismo índice.
                DatabaseIndex index = await entry.Value.WaitAsync(cancellationToken);
                if (DateTimeOffset.UtcNow - index.BuiltAt <= ttl)
                {
                    return index;
                }
                _entries.TryRemove(new KeyValuePair<string, Lazy<Task<DatabaseIndex>>>(key, entry));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Un fallo no queda cacheado: el siguiente intento vuelve a leer.
                _entries.TryRemove(new KeyValuePair<string, Lazy<Task<DatabaseIndex>>>(key, entry));
                throw;
            }
        }

        throw new InvalidOperationException("No se pudo obtener el índice actualizado.");
    }

    public IReadOnlyList<DatabaseIndex> GetLoaded() =>
        _entries.Values
            .Where(e => e.IsValueCreated && e.Value.IsCompletedSuccessfully)
            .Select(e => e.Value.Result)
            .ToList();

    public void Invalidate(string instance, string database)
    {
        _entries.TryRemove(CacheKey(instance, database), out _);
        _logger.LogInformation("Índice invalidado: {Instance}/{Database}", instance, database);
    }

    private async Task<DatabaseIndex> BuildAsync(string instance, string database)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        IReadOnlyList<DatabaseInfo> databases = await _reader.ListDatabasesAsync(instance, CancellationToken.None);
        DatabaseInfo? match = databases.FirstOrDefault(d => d.Name.Equals(database, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new DatabaseNotAccessibleException(instance, database, "No existe, no está ONLINE o el login no tiene acceso.");
        }

        CatalogSnapshot catalog = await _reader.ReadCatalogAsync(instance, match.Name, CancellationToken.None);
        TimeSpan readTime = stopwatch.Elapsed;
        DatabaseIndex index = _builder.Build(catalog, readTime);

        _logger.LogInformation(
            "Índice construido {Instance}/{Database}: {Nodes} nodos, {Edges} aristas en {Elapsed} ms",
            instance, match.Name, index.Graph.NodeCount, index.Graph.EdgeCount, stopwatch.ElapsedMilliseconds);

        return index;
    }

    private static string CacheKey(string instance, string database) => $"{instance}|{database}".ToUpperInvariant();
}
