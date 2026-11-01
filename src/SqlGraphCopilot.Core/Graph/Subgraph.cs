using SqlGraphCopilot.Core.Domain;

namespace SqlGraphCopilot.Core.Graph;

/// <summary>
/// Fragmento del grafo que se envía al cliente. Nunca se envía el grafo completo.
/// </summary>
/// <param name="Truncated">Se alcanzó el tope de nodos; hay más vecinos sin enviar.</param>
/// <param name="FrontierNodeIds">Nodos con vecinos aún no cargados (expandibles).</param>
/// <param name="Depths">Distancia (hops) de cada nodo al nodo de inicio.</param>
public sealed record Subgraph(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    bool Truncated,
    IReadOnlySet<string> FrontierNodeIds,
    IReadOnlyDictionary<string, int> Depths)
{
    public static Subgraph Empty { get; } = new(
        Array.Empty<GraphNode>(),
        Array.Empty<GraphEdge>(),
        false,
        new HashSet<string>(),
        new Dictionary<string, int>());
}
