using SqlGraphCopilot.Domain;
using SqlGraphCopilot.Domain.Graph;

namespace SqlGraphCopilot.Application.Analysis;

/// <summary>
/// Algoritmos clásicos en versión iterativa (sin recursión): con miles de SPs
/// una versión recursiva puede desbordar la pila.
/// </summary>
internal static class GraphAlgorithms
{
    /// <summary>
    /// Puntos de articulación (Tarjan) sobre la proyección no dirigida: nodos cuya caída
    /// separa el grafo en partes. Son candidatos naturales a punto único de fallo.
    /// </summary>
    public static HashSet<string> ArticulationPoints(GraphSnapshot snapshot, Func<GraphNode, bool> include)
    {
        Dictionary<string, List<string>> adjacency = new(StringComparer.OrdinalIgnoreCase);
        foreach (GraphNode node in snapshot.Nodes.Values.Where(include))
        {
            adjacency[node.Id] = new List<string>();
        }
        foreach (string id in adjacency.Keys.ToList())
        {
            foreach (GraphEdge edge in snapshot.Out(id))
            {
                if (adjacency.ContainsKey(edge.TargetId))
                {
                    adjacency[id].Add(edge.TargetId);
                    adjacency[edge.TargetId].Add(id);
                }
            }
        }

        Dictionary<string, int> discovery = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> low = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> parent = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        int time = 0;

        foreach (string root in adjacency.Keys)
        {
            if (discovery.ContainsKey(root))
            {
                continue;
            }

            int rootChildren = 0;
            discovery[root] = low[root] = ++time;
            parent[root] = null;
            Stack<Frame> stack = new();
            stack.Push(new Frame(root));

            while (stack.Count > 0)
            {
                Frame frame = stack.Peek();
                List<string> neighbors = adjacency[frame.Node];

                if (frame.Index < neighbors.Count)
                {
                    string next = neighbors[frame.Index++];
                    if (!discovery.ContainsKey(next))
                    {
                        parent[next] = frame.Node;
                        discovery[next] = low[next] = ++time;
                        if (frame.Node.Equals(root, StringComparison.OrdinalIgnoreCase))
                        {
                            rootChildren++;
                        }
                        stack.Push(new Frame(next));
                    }
                    else if (!next.Equals(parent[frame.Node], StringComparison.OrdinalIgnoreCase))
                    {
                        low[frame.Node] = Math.Min(low[frame.Node], discovery[next]);
                    }
                    continue;
                }

                stack.Pop();
                string? up = parent[frame.Node];
                if (up is null)
                {
                    continue;
                }

                low[up] = Math.Min(low[up], low[frame.Node]);
                if (parent[up] is not null && low[frame.Node] >= discovery[up])
                {
                    result.Add(up);
                }
            }

            if (rootChildren > 1)
            {
                result.Add(root);
            }
        }

        return result;
    }

    /// <summary>Componentes fuertemente conexos (Tarjan) considerando solo las aristas que cumplen el filtro.</summary>
    public static List<List<string>> StronglyConnectedComponents(GraphSnapshot snapshot, Func<GraphEdge, bool> edgeFilter)
    {
        Dictionary<string, int> index = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> low = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> onStack = new(StringComparer.OrdinalIgnoreCase);
        Stack<string> componentStack = new();
        List<List<string>> components = new();
        int counter = 0;

        foreach (string start in snapshot.Nodes.Keys)
        {
            if (index.ContainsKey(start))
            {
                continue;
            }

            Stack<Frame> callStack = new();
            Visit(start);
            callStack.Push(new Frame(start));

            while (callStack.Count > 0)
            {
                Frame frame = callStack.Peek();
                IReadOnlyList<GraphEdge> edges = snapshot.Out(frame.Node);

                if (frame.Index < edges.Count)
                {
                    GraphEdge edge = edges[frame.Index++];
                    if (!edgeFilter(edge))
                    {
                        continue;
                    }

                    string next = edge.TargetId;
                    if (!index.ContainsKey(next))
                    {
                        Visit(next);
                        callStack.Push(new Frame(next));
                    }
                    else if (onStack.Contains(next))
                    {
                        low[frame.Node] = Math.Min(low[frame.Node], index[next]);
                    }
                    continue;
                }

                callStack.Pop();
                if (low[frame.Node] == index[frame.Node])
                {
                    List<string> component = new();
                    string member;
                    do
                    {
                        member = componentStack.Pop();
                        onStack.Remove(member);
                        component.Add(member);
                    }
                    while (!member.Equals(frame.Node, StringComparison.OrdinalIgnoreCase));
                    components.Add(component);
                }

                if (callStack.Count > 0)
                {
                    string caller = callStack.Peek().Node;
                    low[caller] = Math.Min(low[caller], low[frame.Node]);
                }
            }
        }

        return components;

        void Visit(string node)
        {
            index[node] = low[node] = counter++;
            componentStack.Push(node);
            onStack.Add(node);
        }
    }

    /// <summary>
    /// Cadena de ejecución más larga desde cada nodo (EXEC anidados), ignorando ciclos.
    /// Devuelve profundidad y siguiente salto para reconstruir el camino.
    /// </summary>
    public static Dictionary<string, (int Depth, string? Next)> LongestChains(GraphSnapshot snapshot, Func<GraphEdge, bool> edgeFilter, ISet<string> cyclicNodes)
    {
        Dictionary<string, (int Depth, string? Next)> memo = new(StringComparer.OrdinalIgnoreCase);

        foreach (string start in snapshot.Nodes.Keys)
        {
            if (memo.ContainsKey(start))
            {
                continue;
            }

            // DFS iterativo post-orden.
            Stack<Frame> stack = new();
            HashSet<string> inProgress = new(StringComparer.OrdinalIgnoreCase) { start };
            stack.Push(new Frame(start));

            while (stack.Count > 0)
            {
                Frame frame = stack.Peek();
                IReadOnlyList<GraphEdge> edges = snapshot.Out(frame.Node);
                bool pushed = false;

                while (frame.Index < edges.Count)
                {
                    GraphEdge edge = edges[frame.Index++];
                    string next = edge.TargetId;
                    if (!edgeFilter(edge) || cyclicNodes.Contains(next) || memo.ContainsKey(next) || inProgress.Contains(next))
                    {
                        continue;
                    }
                    inProgress.Add(next);
                    stack.Push(new Frame(next));
                    pushed = true;
                    break;
                }

                if (pushed)
                {
                    continue;
                }

                stack.Pop();
                inProgress.Remove(frame.Node);
                int best = 0;
                string? bestNext = null;
                foreach (GraphEdge edge in edges)
                {
                    if (!edgeFilter(edge) || cyclicNodes.Contains(edge.TargetId))
                    {
                        continue;
                    }
                    if (memo.TryGetValue(edge.TargetId, out (int Depth, string? Next) child) && child.Depth + 1 > best)
                    {
                        best = child.Depth + 1;
                        bestNext = edge.TargetId;
                    }
                }
                memo[frame.Node] = (best, bestNext);
            }
        }

        return memo;
    }

    private sealed class Frame
    {
        public Frame(string node) => Node = node;
        public string Node { get; }
        public int Index { get; set; }
    }
}
