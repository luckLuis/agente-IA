using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Catalog;
using SqlGraphCopilot.Application.Settings;
using SqlGraphCopilot.Domain;
using SqlGraphCopilot.Domain.Graph;
using SqlGraphCopilot.Domain.Indexing;

namespace SqlGraphCopilot.Application.Indexing;

/// <summary>
/// Convierte el catálogo real (sys.objects + sys.sql_expression_dependencies + sinónimos + triggers)
/// en el grafo de una base. Las referencias a otras bases o instancias quedan como nodos externos
/// "no expandidos" cuyo Id coincide con el del objeto real en su propio índice.
/// </summary>
public sealed class DatabaseIndexBuilder
{
    private readonly IInstanceRegistry _registry;
    private readonly IReadOnlyList<Regex> _criticalPatterns;

    public DatabaseIndexBuilder(IInstanceRegistry registry, IOptions<SqlGraphCopilotOptions> options)
    {
        _registry = registry;
        _criticalPatterns = options.Value.CriticalNamePatterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            .ToList();
    }

    public DatabaseIndex Build(CatalogSnapshot catalog, TimeSpan readDuration)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string instance = catalog.Instance;
        string database = catalog.Database;

        DependencyGraph graph = new();
        Dictionary<int, string> objectIdToNodeId = new();
        Dictionary<int, CatalogObject> objectsById = new();

        // 1. Nodos locales
        foreach (CatalogObject obj in catalog.Objects)
        {
            NodeKind? kind = CatalogTypeMapper.ToNodeKind(obj.TypeCode);
            if (kind is null)
            {
                continue;
            }

            ObjectKey key = new(instance, database, obj.Schema, obj.Name);
            GraphNode node = new()
            {
                Key = key,
                Kind = kind.Value,
                IsCritical = IsCritical(key),
                HasDynamicSql = obj.HasDynamicSqlHint,
                UsesOpenQuery = obj.UsesOpenQueryHint,
                ObjectId = obj.ObjectId,
                ModifiedAt = obj.ModifiedAt,
                DefinitionHash = obj.DefinitionHash
            };
            graph.AddOrUpdateNode(node);
            objectIdToNodeId[obj.ObjectId] = node.Id;
            objectsById[obj.ObjectId] = obj;
        }

        ReferenceResolver resolver = new(this, graph, instance, database);

        // 2. Triggers: trigger -> tabla sobre la que se dispara
        foreach (CatalogTrigger trigger in catalog.Triggers)
        {
            if (objectIdToNodeId.TryGetValue(trigger.TriggerId, out string? triggerId) &&
                objectIdToNodeId.TryGetValue(trigger.ParentId, out string? parentId))
            {
                graph.AddEdge(triggerId, parentId, DependencyKind.TriggeredBy);
            }
        }

        // 3. Sinónimos: sinónimo -> objeto base (frecuentes para cross-database)
        foreach (CatalogSynonym synonym in catalog.Synonyms)
        {
            if (!objectIdToNodeId.TryGetValue(synonym.ObjectId, out string? synonymId))
            {
                continue;
            }
            string? targetId = resolver.Resolve(synonym.BaseServer, synonym.BaseDatabase, synonym.BaseSchema, synonym.BaseName, null, null);
            if (targetId is not null)
            {
                graph.AddEdge(synonymId, targetId, DependencyKind.SynonymFor);
            }
        }

        // 4. Dependencias del catálogo
        foreach (CatalogDependency dependency in catalog.Dependencies)
        {
            if (!objectIdToNodeId.TryGetValue(dependency.ReferencingId, out string? sourceId))
            {
                continue;
            }

            string? referencingSchema = objectsById.TryGetValue(dependency.ReferencingId, out CatalogObject? source) ? source.Schema : null;

            string? targetId = resolver.Resolve(
                dependency.ReferencedServer,
                dependency.ReferencedDatabase,
                dependency.ReferencedSchema,
                dependency.ReferencedName,
                dependency.ReferencedId is int referencedId && objectIdToNodeId.TryGetValue(referencedId, out string? resolvedId) ? resolvedId : null,
                referencingSchema);

            if (targetId is null)
            {
                continue;
            }

            GraphNode target = graph.GetNode(targetId)!;
            DependencyKind kind = target.Kind switch
            {
                NodeKind.StoredProcedure => DependencyKind.Executes,
                NodeKind.Function => DependencyKind.Calls,
                _ => DependencyKind.References
            };
            graph.AddEdge(sourceId, targetId, kind);
        }

        TimeSpan total = readDuration + (DateTimeOffset.UtcNow - started);
        return new DatabaseIndex(instance, database, graph, objectIdToNodeId, DateTimeOffset.UtcNow, total);
    }

    internal bool IsCritical(ObjectKey key) =>
        _criticalPatterns.Any(p => p.IsMatch(key.DisplayName) || p.IsMatch(key.Name));

    /// <summary>Resuelve una referencia del catálogo a un Id de nodo, creando nodos externos o "rotos" cuando corresponde.</summary>
    private sealed class ReferenceResolver
    {
        private readonly DatabaseIndexBuilder _owner;
        private readonly DependencyGraph _graph;
        private readonly string _instance;
        private readonly string _database;

        public ReferenceResolver(DatabaseIndexBuilder owner, DependencyGraph graph, string instance, string database)
        {
            _owner = owner;
            _graph = graph;
            _instance = instance;
            _database = database;
        }

        public string? Resolve(string? server, string? database, string? schema, string name, string? knownLocalId, string? referencingSchema)
        {
            if (knownLocalId is not null)
            {
                return knownLocalId;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            bool hasServer = !string.IsNullOrWhiteSpace(server);
            string? registeredServer = hasServer ? _owner._registry.ResolveServer(server!) : _instance;
            string targetServer = registeredServer ?? server!;
            string targetDatabase = string.IsNullOrWhiteSpace(database) ? _database : database!;

            bool sameInstance = string.Equals(targetServer, _instance, StringComparison.OrdinalIgnoreCase);
            bool sameDatabase = sameInstance && string.Equals(targetDatabase, _database, StringComparison.OrdinalIgnoreCase);

            if (sameDatabase)
            {
                return ResolveLocal(schema, name, referencingSchema);
            }

            ObjectKey key = new(targetServer, targetDatabase, schema, name);
            GraphNode external = _graph.GetOrAddNode(new GraphNode
            {
                Key = key,
                Kind = NodeKind.External,
                Scope = sameInstance ? ExternalScope.CrossDatabase : ExternalScope.LinkedServer,
                IsCritical = _owner.IsCritical(key)
            });
            return external.Id;
        }

        /// <summary>
        /// Referencia en la misma base que el catálogo no resolvió (sin esquema o diferida):
        /// se intenta el esquema del objeto que referencia, luego dbo. Si no existe, es una referencia rota.
        /// </summary>
        private string ResolveLocal(string? schema, string name, string? referencingSchema)
        {
            IEnumerable<string> candidates = schema is not null
                ? new[] { schema }
                : new[] { referencingSchema, ObjectKey.DefaultSchema }.Where(s => s is not null).Select(s => s!).Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string candidate in candidates)
            {
                ObjectKey key = new(_instance, _database, candidate, name);
                if (_graph.Contains(key.Id))
                {
                    return key.Id;
                }
            }

            ObjectKey missingKey = new(_instance, _database, schema, name);
            GraphNode missing = _graph.GetOrAddNode(new GraphNode
            {
                Key = missingKey,
                Kind = NodeKind.Missing,
                IsCritical = false
            });
            return missing.Id;
        }
    }
}
