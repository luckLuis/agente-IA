using SqlGraphCopilot.Core.Domain;
using SqlGraphCopilot.Core.Indexing;

namespace SqlGraphCopilot.Core.Analysis;

public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

// ---- Impacto ----------------------------------------------------------------------------

public sealed record ImpactedObject(
    string NodeId,
    string Name,
    string Database,
    NodeKind Kind,
    int Depth,
    string ViaNodeId,
    DependencyKind Relation,
    bool IsCritical,
    bool IsOtherDatabase);

/// <summary>Impacto por datos: lectores de las tablas que escribe el objeto analizado.</summary>
public sealed record DataImpactEntry(string TableId, string TableName, IReadOnlyList<string> ReaderIds, IReadOnlyList<string> ReaderNames);

public sealed record ImpactReport(
    string TargetId,
    string TargetName,
    NodeKind TargetKind,
    RiskLevel Level,
    double Score,
    int TotalAffected,
    int CriticalAffected,
    IReadOnlyList<ImpactedObject> Affected,
    IReadOnlyList<DataImpactEntry> DataImpact,
    IReadOnlyList<string> SearchedDatabases,
    IReadOnlyList<string> Warnings);

// ---- Estructura -------------------------------------------------------------------------

public sealed record SpofFinding(
    string NodeId,
    string Name,
    NodeKind Kind,
    int DirectDependents,
    int TotalDependents,
    int Writers,
    bool IsArticulationPoint,
    bool IsCritical,
    double Score,
    IReadOnlyList<string> Reasons);

public sealed record CouplingFinding(string NodeId, string Name, NodeKind Kind, int FanIn, int FanOut, double Instability, int CouplingScore);

public sealed record DuplicateGroup(string DefinitionHash, IReadOnlyList<string> NodeIds, IReadOnlyList<string> Names);

public sealed record SimilarPair(string FirstId, string FirstName, string SecondId, string SecondName, double Similarity, int SharedDependencies);

public sealed record RedundancyReport(IReadOnlyList<DuplicateGroup> ExactDuplicates, IReadOnlyList<SimilarPair> SimilarProcedures);

public sealed record WriteHotspot(string TableId, string TableName, int Writers, int Readers, IReadOnlyList<string> WriterNames);

public sealed record CallChain(string RootId, string RootName, int Depth, IReadOnlyList<string> Path);

public sealed record CycleFinding(IReadOnlyList<string> NodeIds, IReadOnlyList<string> Names);

public sealed record ObjectMetric(string NodeId, string Name, NodeKind Kind, int Value);

public sealed record BottleneckReport(
    IReadOnlyList<WriteHotspot> WriteHotspots,
    IReadOnlyList<CallChain> DeepCallChains,
    IReadOnlyList<CycleFinding> Cycles,
    IReadOnlyList<ObjectMetric> DynamicSqlObjects,
    IReadOnlyList<ObjectMetric> GodProcedures,
    double ClassificationCoverage);

public sealed record BrokenReference(string MissingId, string MissingName, IReadOnlyList<string> ReferencedByIds, IReadOnlyList<string> ReferencedByNames);

public sealed record OverviewReport(
    DatabaseSummary Summary,
    IReadOnlyList<SpofFinding> SinglePointsOfFailure,
    IReadOnlyList<CouplingFinding> Coupling,
    RedundancyReport Redundancy,
    BottleneckReport Bottlenecks,
    IReadOnlyList<BrokenReference> BrokenReferences,
    IReadOnlyList<string> Warnings);

internal static class NodeLabels
{
    /// <summary>"esquema.nombre" para objetos locales; "base.esquema.nombre" (o servidor) para externos.</summary>
    public static string For(GraphNode node, DatabaseIndex index) => node.Kind switch
    {
        NodeKind.LinkedServer => $"[{node.Key.Name}] (servidor vinculado)",
        _ when !index.IsLocal(node) && !node.Key.Server.Equals(index.Instance, StringComparison.OrdinalIgnoreCase)
            => $"{node.Key.Server}.{node.Key.Database}.{node.DisplayName}",
        _ when !index.IsLocal(node) => $"{node.Key.Database}.{node.DisplayName}",
        _ => node.DisplayName
    };
}
