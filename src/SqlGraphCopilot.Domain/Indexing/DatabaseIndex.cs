using System.Collections.Concurrent;
using SqlGraphCopilot.Domain.Graph;

namespace SqlGraphCopilot.Domain.Indexing;

/// <summary>
/// Índice liviano de UNA base: solo nombres, tipos y aristas (sin código).
/// Vive en el servidor; al cliente solo viajan fragmentos (<see cref="Subgraph"/>).
/// </summary>
public sealed class DatabaseIndex(
    string instance,
    string database,
    DependencyGraph graph,
    IReadOnlyDictionary<int, string> objectIdToNodeId,
    DateTimeOffset builtAt,
    TimeSpan buildDuration)
{
    private readonly ConcurrentDictionary<string, (int Version, object Value)> _analysisCache = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<int, string> _objectIdToNodeId = objectIdToNodeId;
    private readonly Dictionary<string, List<string>> _nameLookup = BuildNameLookup(graph.GetNodes());
    private int _version;
    private int _deepScanCompleted;

    public string Instance { get; } = instance;
    public string Database { get; } = database;
    public DependencyGraph Graph { get; } = graph;
    public DateTimeOffset BuiltAt { get; } = builtAt;
    public TimeSpan BuildDuration { get; } = buildDuration;

    /// <summary>Cambia cada vez que la clasificación modifica el grafo (invalida análisis cacheados).</summary>
    public int Version => Volatile.Read(ref _version);

    public bool DeepScanCompleted => Volatile.Read(ref _deepScanCompleted) == 1;

    public string? GetNodeIdByObjectId(int objectId) => _objectIdToNodeId.TryGetValue(objectId, out string? id) ? id : null;

    public bool IsLocal(GraphNode node) => node.Key.IsInDatabase(Instance, Database) && node.Kind is not NodeKind.LinkedServer;

    /// <summary>Coincidencia exacta por "nombre", "esquema.nombre" o Id.</summary>
    public IReadOnlyList<GraphNode> FindByName(string text)
    {
        string normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return Array.Empty<GraphNode>();
        }

        if (Graph.GetNode(normalized) is { } byId)
        {
            return new[] { byId };
        }

        if (!_nameLookup.TryGetValue(normalized, out List<string>? ids))
        {
            return Array.Empty<GraphNode>();
        }

        return ids.Select(Graph.GetNode)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => IsLocal(n) ? 0 : 1)
            .ThenByDescending(n => Graph.FanIn(n.Id))
            .ToList();
    }

    public PagedResult<GraphNode> Search(ObjectSearchQuery query)
    {
        int pageSize = Math.Clamp(query.PageSize, 1, 200);
        int page = Math.Max(1, query.Page);
        string text = query.Text?.Trim() ?? string.Empty;
        HashSet<NodeKind>? kinds = query.Kinds is { Count: > 0 } ? query.Kinds.ToHashSet() : null;

        IEnumerable<GraphNode> candidates = Graph.GetNodes()
            .Where(n => query.IncludeExternal || IsLocal(n))
            .Where(n => kinds is null || kinds.Contains(n.Kind));

        if (text.Length > 0)
        {
            candidates = candidates.Where(n => n.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                               (!IsLocal(n) && n.Key.Database.Contains(text, StringComparison.OrdinalIgnoreCase)));
        }

        List<GraphNode> ordered = candidates
            .OrderBy(n => Rank(n, text))
            .ThenByDescending(n => Graph.FanIn(n.Id))
            .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<GraphNode> items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new PagedResult<GraphNode>(items, page, pageSize, ordered.Count);
    }

    public DatabaseSummary GetSummary()
    {
        IReadOnlyList<GraphNode> nodes = Graph.GetNodes();
        List<GraphNode> local = nodes.Where(IsLocal).ToList();
        List<GraphNode> programmable = local.Where(n => n.IsProgrammable).ToList();

        Dictionary<NodeKind, int> counts = local
            .Where(n => n.Kind is not NodeKind.Missing)
            .GroupBy(n => n.Kind)
            .ToDictionary(g => g.Key, g => g.Count());

        return new DatabaseSummary(
            Instance,
            Database,
            BuiltAt,
            (long)BuildDuration.TotalMilliseconds,
            counts,
            Graph.EdgeCount,
            nodes.Count(n => n.IsExternal),
            nodes.Count(n => n.Kind is NodeKind.LinkedServer),
            local.Count(n => n.Kind is NodeKind.Missing),
            programmable.Count,
            programmable.Count(n => n.IsClassified),
            local.Count(n => n.HasDynamicSql),
            DeepScanCompleted);
    }

    /// <summary>Cachea un análisis global mientras el grafo no cambie.</summary>
    public T GetOrAddAnalysis<T>(string key, Func<T> factory) where T : class
    {
        int version = Version;
        if (_analysisCache.TryGetValue(key, out (int Version, object Value) cached) && cached.Version == version && cached.Value is T hit)
        {
            return hit;
        }

        T value = factory();
        _analysisCache[key] = (version, value);
        return value;
    }

    public void MarkChanged() => Interlocked.Increment(ref _version);

    public void MarkDeepScanCompleted()
    {
        Interlocked.Exchange(ref _deepScanCompleted, 1);
        MarkChanged();
    }

    private int Rank(GraphNode node, string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }
        if (node.Key.Name.Equals(text, StringComparison.OrdinalIgnoreCase) || node.DisplayName.Equals(text, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (node.Key.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        return 2;
    }

    private static Dictionary<string, List<string>> BuildNameLookup(IEnumerable<GraphNode> nodes)
    {
        Dictionary<string, List<string>> lookup = new(StringComparer.OrdinalIgnoreCase);
        foreach (GraphNode node in nodes)
        {
            Add(node.Key.Name, node.Id);
            Add(node.DisplayName, node.Id);
            Add($"{node.Key.Database}.{node.DisplayName}", node.Id);
        }
        return lookup;

        void Add(string key, string id)
        {
            string normalized = Normalize(key);
            if (!lookup.TryGetValue(normalized, out List<string>? ids))
            {
                ids = new List<string>();
                lookup[normalized] = ids;
            }
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                ids.Add(id);
            }
        }
    }

    private static string Normalize(string text) =>
        text.Trim().Replace("[", string.Empty).Replace("]", string.Empty).Trim('.', ' ', '"', '`', '\'', '?', ',', ';', ':');
}

public sealed record ObjectSearchQuery(string? Text, IReadOnlyCollection<NodeKind>? Kinds, bool IncludeExternal, int Page, int PageSize);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed record DatabaseSummary(
    string Instance,
    string Database,
    DateTimeOffset BuiltAt,
    long BuildMilliseconds,
    IReadOnlyDictionary<NodeKind, int> ObjectCounts,
    int EdgeCount,
    int ExternalReferenceCount,
    int LinkedServerCount,
    int BrokenReferenceCount,
    int ProgrammableCount,
    int ClassifiedCount,
    int DynamicSqlCount,
    bool DeepScanCompleted)
{
    public double ClassificationCoverage => ProgrammableCount == 0 ? 1 : ClassifiedCount / (double)ProgrammableCount;
}
