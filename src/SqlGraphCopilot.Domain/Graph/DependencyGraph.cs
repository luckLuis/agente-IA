namespace SqlGraphCopilot.Domain.Graph;

/// <summary>
/// Grafo dirigido "origen depende de destino" con listas de adyacencia de entrada y salida.
/// Corrige el original, que recorría TODAS las aristas por cada nodo visitado (O(V·E)):
/// aquí cada vecino se obtiene en O(1).
/// Es seguro para concurrencia: se lee desde varias peticiones mientras la clasificación lazy lo actualiza.
/// </summary>
public sealed class DependencyGraph
{
    private readonly object _sync = new();
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, GraphEdge>> _outgoing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, GraphEdge>> _incoming = new(StringComparer.OrdinalIgnoreCase);
    private int _edgeCount;

    public int NodeCount { get { lock (_sync) { return _nodes.Count; } } }
    public int EdgeCount { get { lock (_sync) { return _edgeCount; } } }

    public void AddOrUpdateNode(GraphNode node)
    {
        lock (_sync)
        {
            _nodes[node.Id] = node;
            _outgoing.TryAdd(node.Id, new Dictionary<string, GraphEdge>(StringComparer.OrdinalIgnoreCase));
            _incoming.TryAdd(node.Id, new Dictionary<string, GraphEdge>(StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>Agrega el nodo solo si no existe (no pisa un nodo real con un placeholder externo).</summary>
    public GraphNode GetOrAddNode(GraphNode node)
    {
        lock (_sync)
        {
            if (_nodes.TryGetValue(node.Id, out GraphNode? existing))
            {
                return existing;
            }
            AddOrUpdateNode(node);
            return node;
        }
    }

    public bool UpdateNode(string id, Func<GraphNode, GraphNode> update)
    {
        lock (_sync)
        {
            if (!_nodes.TryGetValue(id, out GraphNode? current))
            {
                return false;
            }
            GraphNode updated = update(current);
            if (!string.Equals(updated.Id, current.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("UpdateNode no puede cambiar la identidad del nodo.");
            }
            _nodes[id] = updated;
            return true;
        }
    }

    /// <summary>Agrega la arista o combina sus tipos (OR) si ya existe. Ignora auto-referencias.</summary>
    public bool AddEdge(string sourceId, string targetId, DependencyKind kinds)
    {
        if (kinds == DependencyKind.None || string.Equals(sourceId, targetId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (_sync)
        {
            if (!_nodes.ContainsKey(sourceId) || !_nodes.ContainsKey(targetId))
            {
                return false;
            }

            Dictionary<string, GraphEdge> outEdges = _outgoing[sourceId];
            if (outEdges.TryGetValue(targetId, out GraphEdge? existing))
            {
                kinds |= existing.Kinds;
            }
            else
            {
                _edgeCount++;
            }

            GraphEdge edge = new(_nodes[sourceId].Id, _nodes[targetId].Id, kinds);
            outEdges[targetId] = edge;
            _incoming[targetId][sourceId] = edge;
            return true;
        }
    }

    /// <summary>Reemplaza los tipos de una arista existente (lo usa el clasificador).</summary>
    public bool SetEdgeKinds(string sourceId, string targetId, DependencyKind kinds)
    {
        lock (_sync)
        {
            if (!_outgoing.TryGetValue(sourceId, out Dictionary<string, GraphEdge>? outEdges) ||
                !outEdges.TryGetValue(targetId, out GraphEdge? existing))
            {
                return false;
            }
            GraphEdge edge = existing with { Kinds = kinds };
            outEdges[targetId] = edge;
            _incoming[targetId][sourceId] = edge;
            return true;
        }
    }

    public GraphNode? GetNode(string id)
    {
        lock (_sync)
        {
            return _nodes.TryGetValue(id, out GraphNode? node) ? node : null;
        }
    }

    public bool Contains(string id)
    {
        lock (_sync) { return _nodes.ContainsKey(id); }
    }

    public IReadOnlyList<GraphNode> GetNodes()
    {
        lock (_sync) { return _nodes.Values.ToList(); }
    }

    public IReadOnlyList<GraphEdge> GetEdges()
    {
        lock (_sync) { return _outgoing.Values.SelectMany(e => e.Values).ToList(); }
    }

    public IReadOnlyList<GraphEdge> GetOutgoing(string id)
    {
        lock (_sync)
        {
            return _outgoing.TryGetValue(id, out Dictionary<string, GraphEdge>? edges) ? edges.Values.ToList() : Array.Empty<GraphEdge>();
        }
    }

    public IReadOnlyList<GraphEdge> GetIncoming(string id)
    {
        lock (_sync)
        {
            return _incoming.TryGetValue(id, out Dictionary<string, GraphEdge>? edges) ? edges.Values.ToList() : Array.Empty<GraphEdge>();
        }
    }

    public int FanIn(string id)
    {
        lock (_sync) { return _incoming.TryGetValue(id, out Dictionary<string, GraphEdge>? e) ? e.Count : 0; }
    }

    public int FanOut(string id)
    {
        lock (_sync) { return _outgoing.TryGetValue(id, out Dictionary<string, GraphEdge>? e) ? e.Count : 0; }
    }

    /// <summary>Copia inmutable para análisis pesados fuera del lock.</summary>
    public GraphSnapshot Snapshot()
    {
        lock (_sync)
        {
            Dictionary<string, GraphNode> nodes = new(_nodes, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, IReadOnlyList<GraphEdge>> outgoing = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, IReadOnlyList<GraphEdge>> incoming = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Dictionary<string, GraphEdge>> pair in _outgoing)
            {
                outgoing[pair.Key] = pair.Value.Values.ToList();
            }
            foreach (KeyValuePair<string, Dictionary<string, GraphEdge>> pair in _incoming)
            {
                incoming[pair.Key] = pair.Value.Values.ToList();
            }
            return new GraphSnapshot(nodes, outgoing, incoming);
        }
    }

    /// <summary>
    /// Vecindario por niveles (BFS) con tope de nodos: es la base de la carga on-demand.
    /// Los vecinos más referenciados entran primero, así un recorte conserva lo importante.
    /// </summary>
    public Subgraph GetNeighborhood(string startId, int depth, TraversalDirection direction, int maxNodes)
    {
        GraphSnapshot snapshot = Snapshot();
        if (!snapshot.Nodes.ContainsKey(startId))
        {
            return Subgraph.Empty;
        }

        string start = snapshot.Nodes[startId].Id;
        Dictionary<string, int> depths = new(StringComparer.OrdinalIgnoreCase) { [start] = 0 };
        Queue<string> queue = new();
        queue.Enqueue(start);
        bool truncated = false;

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            int currentDepth = depths[current];
            if (currentDepth >= depth)
            {
                continue;
            }

            IEnumerable<string> neighbors = snapshot.Neighbors(current, direction)
                .OrderByDescending(snapshot.FanIn)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase);

            foreach (string neighbor in neighbors)
            {
                if (depths.ContainsKey(neighbor))
                {
                    continue;
                }
                if (depths.Count >= maxNodes)
                {
                    truncated = true;
                    break;
                }
                depths[neighbor] = currentDepth + 1;
                queue.Enqueue(neighbor);
            }
        }

        return snapshot.Induced(depths) with { Truncated = truncated };
    }

    /// <summary>Subgrafo inducido por un conjunto de nodos (todas las aristas entre ellos).</summary>
    public Subgraph GetInduced(IEnumerable<string> ids)
    {
        GraphSnapshot snapshot = Snapshot();
        Dictionary<string, int> depths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string id in ids)
        {
            if (snapshot.Nodes.TryGetValue(id, out GraphNode? node))
            {
                depths[node.Id] = 0;
            }
        }
        return snapshot.Induced(depths);
    }

    /// <summary>
    /// Recorrido inverso (quién depende de mí), con la profundidad y el nodo por el que se llegó.
    /// Es el "radio de impacto" estructural.
    /// </summary>
    public IReadOnlyList<ReachedNode> GetDependents(string startId, int maxDepth)
    {
        GraphSnapshot snapshot = Snapshot();
        return snapshot.GetDependents(startId, maxDepth);
    }
}

public sealed record ReachedNode(string NodeId, int Depth, string ViaNodeId, DependencyKind Relation);
