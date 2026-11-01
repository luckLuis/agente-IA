using SqlGraphCopilot.Core.Domain;
using SqlGraphCopilot.Core.Graph;

namespace SqlGraphCopilot.Core.Tests.Graph;

public sealed class DependencyGraphTests
{
    private static GraphNode Node(string name, NodeKind kind = NodeKind.Table) =>
        new() { Key = new ObjectKey("DEV", "Core", "dbo", name), Kind = kind };

    private static string Id(string name) => new ObjectKey("DEV", "Core", "dbo", name).Id;

    private static DependencyGraph Chain()
    {
        // sp_C -> sp_B -> sp_A -> Tabla ; sp_X -> Tabla
        DependencyGraph graph = new();
        foreach (string name in new[] { "Tabla", "sp_A", "sp_B", "sp_C", "sp_X" })
        {
            graph.AddOrUpdateNode(Node(name, name == "Tabla" ? NodeKind.Table : NodeKind.StoredProcedure));
        }
        graph.AddEdge(Id("sp_A"), Id("Tabla"), DependencyKind.Reads);
        graph.AddEdge(Id("sp_B"), Id("sp_A"), DependencyKind.Executes);
        graph.AddEdge(Id("sp_C"), Id("sp_B"), DependencyKind.Executes);
        graph.AddEdge(Id("sp_X"), Id("Tabla"), DependencyKind.Writes);
        return graph;
    }

    [Fact]
    public void ObjectKey_ignora_mayusculas_y_corchetes()
    {
        ObjectKey a = new("DEV", "Core", "dbo", "Clientes");
        ObjectKey b = ObjectKey.Parse("dev|CORE|[dbo]|clientes");
        Assert.Equal(a, b);
        Assert.Equal(a.Id, b.Id);
        Assert.False(ObjectKey.TryParse("solo|tres|partes", out _));
    }

    [Fact]
    public void AddEdge_combina_tipos_e_ignora_autoreferencias()
    {
        DependencyGraph graph = Chain();
        graph.AddEdge(Id("sp_A"), Id("Tabla"), DependencyKind.Writes);
        graph.AddEdge(Id("sp_A"), Id("sp_A"), DependencyKind.Executes);

        GraphEdge edge = Assert.Single(graph.GetOutgoing(Id("sp_A")));
        Assert.True(edge.Has(DependencyKind.Reads));
        Assert.True(edge.Has(DependencyKind.Writes));
        Assert.Equal(4, graph.EdgeCount);
    }

    [Fact]
    public void Dependientes_transitivos_con_profundidad()
    {
        IReadOnlyList<ReachedNode> reached = Chain().GetDependents(Id("Tabla"), 10);

        Assert.Equal(4, reached.Count);
        Assert.Equal(1, reached.Single(r => r.NodeId == Id("sp_A")).Depth);
        Assert.Equal(3, reached.Single(r => r.NodeId == Id("sp_C")).Depth);
        Assert.Equal(Id("sp_B"), reached.Single(r => r.NodeId == Id("sp_C")).ViaNodeId);
    }

    [Fact]
    public void Dependientes_respeta_profundidad_maxima()
    {
        IReadOnlyList<ReachedNode> reached = Chain().GetDependents(Id("Tabla"), 1);
        Assert.Equal(2, reached.Count);
    }

    [Fact]
    public void Vecindario_marca_frontera_y_recorta_por_tope()
    {
        DependencyGraph graph = Chain();

        Subgraph oneHop = graph.GetNeighborhood(Id("sp_A"), 1, TraversalDirection.Both, 100);
        Assert.Equal(3, oneHop.Nodes.Count);                    // sp_A, Tabla, sp_B
        Assert.Contains(Id("Tabla"), oneHop.FrontierNodeIds);    // sp_X aún no cargado
        Assert.Contains(Id("sp_B"), oneHop.FrontierNodeIds);     // sp_C aún no cargado
        Assert.False(oneHop.Truncated);

        Subgraph capped = graph.GetNeighborhood(Id("Tabla"), 3, TraversalDirection.Both, 2);
        Assert.Equal(2, capped.Nodes.Count);
        Assert.True(capped.Truncated);
    }

    [Fact]
    public void Vecindario_por_direccion()
    {
        Subgraph outgoing = Chain().GetNeighborhood(Id("sp_B"), 5, TraversalDirection.Outgoing, 100);
        string[] expected = new[] { Id("sp_A"), Id("sp_B"), Id("Tabla") }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        string[] actual = outgoing.Nodes.Select(n => n.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }
}
