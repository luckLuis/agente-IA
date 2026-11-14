using SqlGraphCopilot.Application.Exploration;
using SqlGraphCopilot.Domain;
using SqlGraphCopilot.Domain.Graph;

namespace SqlGraphCopilot.Api.Contracts;

public sealed record InstanceDto(string Name, string Environment, string? Description);

public sealed record DatabaseDto(string Name, int CompatibilityLevel);

public sealed record NodeDto(
    string Id,
    string Label,
    string Server,
    string Database,
    string Schema,
    string Name,
    string Kind,
    string Scope,
    bool IsExternal,
    bool IsCritical,
    bool HasDynamicSql,
    bool UsesOpenQuery,
    bool IsClassified,
    int FanIn,
    int FanOut,
    bool HasMore,
    int? Depth);

public sealed record EdgeDto(string From, string To, IReadOnlyList<string> Kinds);

public sealed record SubgraphDto(
    string? Instance,
    string? Database,
    IReadOnlyList<NodeDto> Nodes,
    IReadOnlyList<EdgeDto> Edges,
    bool Truncated,
    string? Message);

public sealed record ChatRequestDto(string Instance, string Database, string Message, string? FocusNodeId);

public sealed record DatabaseScopeRequest(string Instance, string Database);

public sealed record SqlScriptRequest(string Script);

/// <summary>Mapeo manual (propiedad por propiedad), sin librerías de mapeo dinámico.</summary>
public static class ApiMapper
{
    public static SubgraphDto ToDto(ExplorationResult result)
    {
        Subgraph subgraph = result.Subgraph;
        List<NodeDto> nodes = new(subgraph.Nodes.Count);
        foreach (GraphNode node in subgraph.Nodes)
        {
            NodeDegree degree = result.Degrees.TryGetValue(node.Id, out NodeDegree? d) ? d : new NodeDegree(0, 0);
            int? depth = subgraph.Depths.TryGetValue(node.Id, out int value) ? value : null;
            nodes.Add(ToDto(node, degree, subgraph.FrontierNodeIds.Contains(node.Id) || node.IsExternal, depth, result.Instance, result.Database));
        }

        List<EdgeDto> edges = new(subgraph.Edges.Count);
        foreach (GraphEdge edge in subgraph.Edges)
        {
            edges.Add(new EdgeDto(edge.SourceId, edge.TargetId, KindNames(edge.Kinds)));
        }

        return new SubgraphDto(result.Instance, result.Database, nodes, edges, subgraph.Truncated, result.Message);
    }

    public static NodeDto ToDto(GraphNode node, NodeDegree degree, bool hasMore, int? depth, string? instance, string? database)
    {
        bool otherInstance = instance is not null && !node.Key.Server.Equals(instance, StringComparison.OrdinalIgnoreCase);
        bool otherDatabase = database is not null && !node.Key.Database.Equals(database, StringComparison.OrdinalIgnoreCase);

        string label = node.Kind switch
        {
            NodeKind.LinkedServer => node.Key.Name,
            _ when otherInstance => $"{node.Key.Server}.{node.Key.Database}.{node.DisplayName}",
            _ when otherDatabase => $"{node.Key.Database}.{node.DisplayName}",
            _ => node.DisplayName
        };

        return new NodeDto(
            node.Id,
            label,
            node.Key.Server,
            node.Key.Database,
            node.Key.Schema,
            node.Key.Name,
            node.Kind.ToString(),
            node.Scope.ToString(),
            node.IsExternal,
            node.IsCritical,
            node.HasDynamicSql,
            node.UsesOpenQuery,
            node.IsClassified,
            degree.FanIn,
            degree.FanOut,
            hasMore,
            depth);
    }

    public static IReadOnlyList<string> KindNames(DependencyKind kinds) =>
        Enum.GetValues<DependencyKind>()
            .Where(k => k != DependencyKind.None && (kinds & k) == k)
            .Select(k => k.ToString())
            .ToList();
}
