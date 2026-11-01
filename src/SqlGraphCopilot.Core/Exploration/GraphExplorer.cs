using Microsoft.Extensions.Options;
using SqlGraphCopilot.Core.Domain;
using SqlGraphCopilot.Core.Graph;
using SqlGraphCopilot.Core.Indexing;
using SqlGraphCopilot.Core.Metadata;

namespace SqlGraphCopilot.Core.Exploration;

public enum ExplorationStatus
{
    Ok,
    NotFound,

    /// <summary>La rama pertenece a una instancia/base que no se puede cargar (no registrada o sin acceso).</summary>
    Unreachable
}

public sealed record NodeDegree(int FanIn, int FanOut);

public sealed record ExplorationResult(
    ExplorationStatus Status,
    string? Message,
    string? Instance,
    string? Database,
    Subgraph Subgraph,
    IReadOnlyDictionary<string, NodeDegree> Degrees)
{
    public static ExplorationResult Fail(ExplorationStatus status, string message) =>
        new(status, message, null, null, Subgraph.Empty, new Dictionary<string, NodeDegree>());
}

/// <summary>
/// Carga on-demand: el cliente pide fragmentos (hubs iniciales, vecindario de un nodo, rama externa)
/// en lugar del grafo completo. Un nodo externo se expande cargando SOLO el índice de su base.
/// </summary>
public sealed class GraphExplorer
{
    private readonly IDatabaseIndexProvider _indexes;
    private readonly DependencyClassifier _classifier;
    private readonly IInstanceRegistry _registry;
    private readonly SqlGraphCopilotOptions _options;

    public GraphExplorer(
        IDatabaseIndexProvider indexes,
        DependencyClassifier classifier,
        IInstanceRegistry registry,
        IOptions<SqlGraphCopilotOptions> options)
    {
        _indexes = indexes;
        _classifier = classifier;
        _registry = registry;
        _options = options.Value;
    }

    /// <summary>Vista inicial: los objetos más referenciados de la base y las relaciones entre ellos.</summary>
    public async Task<ExplorationResult> GetHubsAsync(string instance, string database, int? top, CancellationToken cancellationToken)
    {
        DatabaseIndex index = await _indexes.GetAsync(instance, database, cancellationToken);
        int count = Math.Clamp(top ?? _options.DefaultHubCount, 1, _options.MaxNodesPerRequest);

        GraphSnapshot snapshot = index.Graph.Snapshot();
        List<string> hubIds = snapshot.Nodes.Values
            .Where(n => index.IsLocal(n) && n.Kind is not NodeKind.Missing)
            .OrderByDescending(n => snapshot.FanIn(n.Id))
            .ThenByDescending(n => snapshot.FanOut(n.Id))
            .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .Select(n => n.Id)
            .ToList();

        Subgraph subgraph = index.Graph.GetInduced(hubIds);
        string? message = subgraph.Nodes.Count == 0 ? "La base no tiene objetos de usuario con dependencias." : null;
        return new ExplorationResult(ExplorationStatus.Ok, message, index.Instance, index.Database, subgraph, Degrees(index, subgraph));
    }

    /// <summary>
    /// Vecindario de un nodo a N saltos. Si el Id pertenece a otra base o instancia,
    /// carga únicamente esa rama (índice de esa base) — es la expansión de nodos externos.
    /// </summary>
    public async Task<ExplorationResult> GetNeighborhoodAsync(
        string nodeId,
        int? depth,
        TraversalDirection direction,
        int? maxNodes,
        CancellationToken cancellationToken)
    {
        if (!ObjectKey.TryParse(nodeId, out ObjectKey? parsed))
        {
            return ExplorationResult.Fail(ExplorationStatus.NotFound, $"Id de objeto inválido: '{nodeId}'.");
        }

        ObjectKey key = parsed!;
        if (key.Database == "*")
        {
            return ExplorationResult.Fail(
                ExplorationStatus.Unreachable,
                $"'{key.Name}' es un servidor vinculado usado con OPENQUERY/EXEC AT: la consulta remota es texto y sus objetos no se pueden conocer desde el catálogo.");
        }

        string? instance = _registry.ResolveServer(key.Server);
        if (instance is null)
        {
            return ExplorationResult.Fail(
                ExplorationStatus.Unreachable,
                $"El servidor '{key.Server}' no está registrado. Agréguelo a SqlGraphCopilot:Instances (o como alias en LinkedServerAliases de la instancia que corresponda) para expandir esta rama.");
        }

        DatabaseIndex index;
        try
        {
            index = await _indexes.GetAsync(instance, key.Database, cancellationToken);
        }
        catch (DatabaseNotAccessibleException ex)
        {
            return ExplorationResult.Fail(ExplorationStatus.Unreachable, ex.Message);
        }
        catch (DataSourceUnavailableException ex)
        {
            return ExplorationResult.Fail(ExplorationStatus.Unreachable, ex.Message);
        }

        ObjectKey localKey = new(instance, key.Database, key.Schema, key.Name);
        GraphNode? node = index.Graph.GetNode(localKey.Id);
        if (node is null)
        {
            return ExplorationResult.Fail(
                ExplorationStatus.NotFound,
                $"'{key.DisplayName}' no existe en {instance}/{key.Database}. Probablemente es una referencia rota en el objeto de origen.");
        }

        int hops = Math.Clamp(depth ?? _options.DefaultNeighborhoodDepth, 1, _options.MaxNeighborhoodDepth);
        int limit = Math.Clamp(maxNodes ?? _options.MaxNodesPerRequest, 10, _options.MaxNodesPerRequest);

        if (_options.AutoClassifyOnExpand)
        {
            // El nodo y sus vecinos programables: así las aristas visibles ya dicen lee / escribe / ejecuta.
            IEnumerable<string> toClassify = new[] { node.Id }
                .Concat(index.Graph.GetIncoming(node.Id).Select(e => e.SourceId))
                .Concat(index.Graph.GetOutgoing(node.Id).Select(e => e.TargetId));
            await _classifier.EnsureClassifiedAsync(index, toClassify, _options.MaxAutoClassifyPerRequest, cancellationToken);
        }

        Subgraph subgraph = index.Graph.GetNeighborhood(node.Id, hops, direction, limit);
        string? message = subgraph.Truncated
            ? $"Se muestran {subgraph.Nodes.Count} nodos (tope por petición). Expanda nodos concretos para ver más."
            : null;

        return new ExplorationResult(ExplorationStatus.Ok, message, index.Instance, index.Database, subgraph, Degrees(index, subgraph));
    }

    private static IReadOnlyDictionary<string, NodeDegree> Degrees(DatabaseIndex index, Subgraph subgraph) =>
        subgraph.Nodes.ToDictionary(
            n => n.Id,
            n => new NodeDegree(index.Graph.FanIn(n.Id), index.Graph.FanOut(n.Id)),
            StringComparer.OrdinalIgnoreCase);
}
