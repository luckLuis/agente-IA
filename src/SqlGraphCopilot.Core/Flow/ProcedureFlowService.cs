using System.Collections.Concurrent;
using SqlGraphCopilot.Core.Metadata;

namespace SqlGraphCopilot.Core.Flow;

/// <summary>Obtiene el código de un procedimiento desde el catálogo y lo analiza. Registrar como singleton.</summary>
public sealed class ProcedureFlowService
{
    private static readonly TimeSpan IndexLifetime = TimeSpan.FromMinutes(5);

    private readonly ICatalogReader _catalog;
    private readonly ConcurrentDictionary<string, (DateTime At, Dictionary<string, int> Ids)> _indexes = new();

    public ProcedureFlowService(ICatalogReader catalog)
    {
        _catalog = catalog;
    }

    /// <summary>Null si el objeto no existe en la base.</summary>
    public async Task<ProcedureFlowReport?> AnalyzeAsync(string instance, string database, string schema, string name, CancellationToken cancellationToken)
    {
        int? objectId = await ResolveAsync(instance, database, schema, name, cancellationToken);
        if (objectId is null) return null;

        string? definition = await _catalog.ReadDefinitionAsync(instance, database, objectId.Value, cancellationToken);
        if (string.IsNullOrWhiteSpace(definition))
        {
            return new ProcedureFlowReport(schema, name, [], [], [],
                ["No se puede leer el código del objeto (está cifrado o no hay permiso VIEW DEFINITION)."]);
        }

        return ProcedureFlowAnalyzer.Analyze(schema, name, definition);
    }

    private async Task<int?> ResolveAsync(string instance, string database, string schema, string name, CancellationToken cancellationToken)
    {
        string key = $"{instance}|{database}".ToLowerInvariant();
        if (!_indexes.TryGetValue(key, out var entry) || DateTime.UtcNow - entry.At > IndexLifetime)
        {
            CatalogSnapshot snapshot = await _catalog.ReadCatalogAsync(instance, database, cancellationToken);
            Dictionary<string, int> ids = new(StringComparer.OrdinalIgnoreCase);
            foreach (CatalogObject item in snapshot.Objects)
            {
                ids[$"{item.Schema}.{item.Name}"] = item.ObjectId;
            }
            entry = (DateTime.UtcNow, ids);
            _indexes[key] = entry;
        }

        return entry.Ids.TryGetValue($"{schema}.{name}", out int id) ? id : null;
    }
}
