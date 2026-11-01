using SqlGraphCopilot.Core.Domain;
using SqlGraphCopilot.Core.Graph;
using SqlGraphCopilot.Core.Indexing;
using SqlGraphCopilot.Core.Metadata;

namespace SqlGraphCopilot.Core.Analysis;

/// <summary>
/// "¿Qué se rompe si modifico X?"
///  - Impacto estructural: quién depende de X, en cascada (vistas, SPs que lo ejecutan, triggers...).
///  - Impacto por datos: si X escribe tablas, quién lee esas tablas.
///  - Impacto en otras bases: dependientes en las bases ya cargadas que referencian X desde fuera.
/// </summary>
public sealed class ImpactAnalyzer
{
    public const int DefaultMaxDepth = 6;
    private const int MaxDataReaders = 25;

    private readonly IDatabaseIndexProvider _indexes;
    private readonly IInstanceRegistry _registry;

    public ImpactAnalyzer(IDatabaseIndexProvider indexes, IInstanceRegistry registry)
    {
        _indexes = indexes;
        _registry = registry;
    }

    public async Task<ImpactReport> AnalyzeAsync(string nodeId, int? maxDepth, CancellationToken cancellationToken)
    {
        ObjectKey key = ObjectKey.Parse(nodeId);
        string instance = _registry.ResolveServer(key.Server) ?? throw new InstanceNotFoundException(key.Server);
        DatabaseIndex index = await _indexes.GetAsync(instance, key.Database, cancellationToken);
        return Analyze(index, new ObjectKey(instance, key.Database, key.Schema, key.Name).Id, maxDepth ?? DefaultMaxDepth, _indexes.GetLoaded());
    }

    public ImpactReport Analyze(DatabaseIndex index, string nodeId, int maxDepth, IReadOnlyList<DatabaseIndex> otherLoadedIndexes)
    {
        GraphNode target = index.Graph.GetNode(nodeId) ?? throw new ObjectNotFoundException(nodeId);
        int depthLimit = Math.Clamp(maxDepth, 1, 15);
        GraphSnapshot snapshot = index.Graph.Snapshot();

        List<ImpactedObject> affected = snapshot.GetDependents(target.Id, depthLimit)
            .Select(r => ToImpacted(snapshot.Nodes[r.NodeId], r, index, isOtherDatabase: false))
            .ToList();

        // Dependientes en OTRAS bases ya cargadas que referencian este objeto como externo.
        List<string> searched = new() { $"{index.Instance}/{index.Database}" };
        foreach (DatabaseIndex other in otherLoadedIndexes.Where(o => !ReferenceEquals(o, index)))
        {
            searched.Add($"{other.Instance}/{other.Database}");
            if (!other.Graph.Contains(target.Id))
            {
                continue;
            }
            GraphSnapshot otherSnapshot = other.Graph.Snapshot();
            affected.AddRange(otherSnapshot.GetDependents(target.Id, depthLimit)
                .Select(r => ToImpacted(otherSnapshot.Nodes[r.NodeId], r, other, isOtherDatabase: true)));
        }

        List<DataImpactEntry> dataImpact = ComputeDataImpact(snapshot, target, index);
        double rawScore = affected.Sum(Weight) + dataImpact.Sum(d => d.ReaderIds.Count * 5.0);
        int criticalCount = affected.Count(a => a.IsCritical);

        return new ImpactReport(
            target.Id,
            NodeLabels.For(target, index),
            target.Kind,
            ToLevel(rawScore),
            Math.Round(Math.Min(rawScore, 100), 1),
            affected.Count,
            criticalCount,
            affected,
            dataImpact,
            searched,
            BuildWarnings(index, snapshot, target, affected));
    }

    public static RiskLevel ToLevel(double score) => score switch
    {
        >= 100 => RiskLevel.Critical,
        >= 50 => RiskLevel.High,
        >= 20 => RiskLevel.Medium,
        _ => RiskLevel.Low
    };

    private static ImpactedObject ToImpacted(GraphNode node, ReachedNode reached, DatabaseIndex index, bool isOtherDatabase) => new(
        node.Id,
        NodeLabels.For(node, index),
        node.Key.Database,
        node.Kind,
        reached.Depth,
        reached.ViaNodeId,
        reached.Relation,
        node.IsCritical,
        isOtherDatabase);

    /// <summary>Peso por tipo, multiplicado si es crítico y atenuado por la distancia.</summary>
    private static double Weight(ImpactedObject item)
    {
        double weight = item.Kind switch
        {
            NodeKind.Table => 15,
            NodeKind.Trigger => 12,
            NodeKind.StoredProcedure => 10,
            NodeKind.Function => 10,
            NodeKind.View => 8,
            NodeKind.Synonym => 4,
            NodeKind.External or NodeKind.LinkedServer => 20,
            _ => 0
        };
        if (item.IsCritical)
        {
            weight *= 2.5;
        }
        if (item.IsOtherDatabase)
        {
            weight *= 1.5; // romper otra base suele detectarse tarde
        }
        return weight / item.Depth;
    }

    private static List<DataImpactEntry> ComputeDataImpact(GraphSnapshot snapshot, GraphNode target, DatabaseIndex index)
    {
        if (!target.IsProgrammable)
        {
            return new List<DataImpactEntry>();
        }

        List<DataImpactEntry> entries = new();
        foreach (GraphEdge write in snapshot.Out(target.Id).Where(e => e.Has(DependencyKind.Writes)))
        {
            GraphNode table = snapshot.Nodes[write.TargetId];
            List<GraphNode> readers = snapshot.In(table.Id)
                .Where(e => !e.SourceId.Equals(target.Id, StringComparison.OrdinalIgnoreCase))
                .Where(e => e.Has(DependencyKind.Reads) || e.Has(DependencyKind.References) || e.Has(DependencyKind.SynonymFor))
                .Select(e => snapshot.Nodes[e.SourceId])
                .OrderByDescending(n => n.IsCritical)
                .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxDataReaders)
                .ToList();

            if (readers.Count > 0)
            {
                entries.Add(new DataImpactEntry(
                    table.Id,
                    NodeLabels.For(table, index),
                    readers.Select(r => r.Id).ToList(),
                    readers.Select(r => NodeLabels.For(r, index)).ToList()));
            }
        }
        return entries;
    }

    private static List<string> BuildWarnings(DatabaseIndex index, GraphSnapshot snapshot, GraphNode target, List<ImpactedObject> affected)
    {
        List<string> warnings = new();

        if (target.Kind is NodeKind.Missing)
        {
            warnings.Add($"{target.DisplayName} no existe en la base: es una referencia rota. Los objetos que lo usan fallarán al ejecutarse.");
        }

        if (target.IsExternal)
        {
            warnings.Add($"{NodeLabels.For(target, index)} pertenece a otra base; expanda el nodo para analizarlo con su índice completo.");
        }

        int dynamicSql = snapshot.Nodes.Values.Count(n => index.IsLocal(n) && n.HasDynamicSql);
        if (dynamicSql > 0)
        {
            warnings.Add($"{dynamicSql} objeto(s) de la base usan SQL dinámico: pueden depender de {target.DisplayName} sin que el catálogo lo registre.");
        }

        int unclassified = affected.Count(a => !a.IsOtherDatabase &&
                                               snapshot.Nodes.TryGetValue(a.NodeId, out GraphNode? n) &&
                                               n.IsProgrammable && !n.IsClassified);
        if (unclassified > 0)
        {
            warnings.Add($"{unclassified} objeto(s) afectados aún no están clasificados (lee/escribe). Ejecute el escaneo profundo para un análisis completo.");
        }

        warnings.Add("Dependencias desde aplicaciones (.NET, reportes, jobs) no están en el catálogo SQL y no se incluyen.");
        return warnings;
    }
}
