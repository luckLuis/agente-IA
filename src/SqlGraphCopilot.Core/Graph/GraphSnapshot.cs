using SqlGraphCopilot.Core.Domain;

namespace SqlGraphCopilot.Core.Graph;

/// <summary>Vista inmutable del grafo para algoritmos (sin locks).</summary>
public sealed class GraphSnapshot
{
    private static readonly IReadOnlyList<GraphEdge> NoEdges = Array.Empty<GraphEdge>();

    public GraphSnapshot(
        IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyDictionary<string, IReadOnlyList<GraphEdge>> outgoing,
        IReadOnlyDictionary<string, IReadOnlyList<GraphEdge>> incoming)
    {
        Nodes = nodes;
        Outgoing = outgoing;
        Incoming = incoming;
    }

    public IReadOnlyDictionary<string, GraphNode> Nodes { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<GraphEdge>> Outgoing { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<GraphEdge>> Incoming { get; }

    public IReadOnlyList<GraphEdge> Out(string id) => Outgoing.TryGetValue(id, out IReadOnlyList<GraphEdge>? e) ? e : NoEdges;
    public IReadOnlyList<GraphEdge> In(string id) => Incoming.TryGetValue(id, out IReadOnlyList<GraphEdge>? e) ? e : NoEdges;
    public int FanIn(string id) => In(id).Count;
    public int FanOut(string id) => Out(id).Count;

    public IEnumerable<string> Neighbors(string id, TraversalDirection direction)
    {
        if (direction is TraversalDirection.Outgoing or TraversalDirection.Both)
        {
            foreach (GraphEdge edge in Out(id))
            {
                yield return edge.TargetId;
            }
        }
        if (direction is TraversalDirection.Incoming or TraversalDirection.Both)
        {
            foreach (GraphEdge edge in In(id))
            {
                yield return edge.SourceId;
            }
        }
    }

    public IReadOnlyList<ReachedNode> GetDependents(string startId, int maxDepth)
    {
        if (!Nodes.TryGetValue(startId, out GraphNode? start))
        {
            return Array.Empty<ReachedNode>();
        }

        Dictionary<string, ReachedNode> reached = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { start.Id };
        Queue<(string Id, int Depth)> queue = new();
        queue.Enqueue((start.Id, 0));

        while (queue.Count > 0)
        {
            (string current, int depth) = queue.Dequeue();
            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (GraphEdge edge in In(current))
            {
                if (visited.Add(edge.SourceId))
                {
                    reached[edge.SourceId] = new ReachedNode(edge.SourceId, depth + 1, current, edge.Kinds);
                    queue.Enqueue((edge.SourceId, depth + 1));
                }
            }
        }

        return reached.Values.OrderBy(r => r.Depth).ThenBy(r => r.NodeId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal Subgraph Induced(Dictionary<string, int> depths)
    {
        if (depths.Count == 0)
        {
            return Subgraph.Empty;
        }

        List<GraphNode> nodes = new(depths.Count);
        List<GraphEdge> edges = new();
        HashSet<string> frontier = new(StringComparer.OrdinalIgnoreCase);

        foreach (string id in depths.Keys)
        {
            nodes.Add(Nodes[id]);
            int includedNeighbors = 0;
            foreach (GraphEdge edge in Out(id))
            {
                if (depths.ContainsKey(edge.TargetId))
                {
                    edges.Add(edge);
                    includedNeighbors++;
                }
            }
            foreach (GraphEdge edge in In(id))
            {
                if (depths.ContainsKey(edge.SourceId))
                {
                    includedNeighbors++;
                }
            }

            // Tiene vecinos sin cargar: el frontend lo marca como expandible.
            if (FanIn(id) + FanOut(id) > includedNeighbors)
            {
                frontier.Add(id);
            }
        }

        return new Subgraph(nodes, edges, false, frontier, depths);
    }
}
