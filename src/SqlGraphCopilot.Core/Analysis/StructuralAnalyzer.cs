using SqlGraphCopilot.Core.Domain;
using SqlGraphCopilot.Core.Graph;
using SqlGraphCopilot.Core.Indexing;

namespace SqlGraphCopilot.Core.Analysis;

/// <summary>
/// Análisis global de una base: puntos únicos de fallo, acoplamiento, redundancias,
/// cuellos de botella y referencias rotas. Trabaja sobre el índice del servidor (nunca en el cliente)
/// y cachea resultados mientras el grafo no cambie.
/// </summary>
public sealed class StructuralAnalyzer
{
    private const int SpofCandidatePool = 60;
    private const int GodProcedureFanOut = 15;
    private const int DeepChainThreshold = 4;
    private const double SimilarityThreshold = 0.75;
    private const int MinDependenciesForSimilarity = 3;
    private const int MaxPostingList = 150;
    private const int MaxPairEvaluations = 2_000_000;

    public IReadOnlyList<SpofFinding> GetSinglePointsOfFailure(DatabaseIndex index, int top = 20) =>
        index.GetOrAddAnalysis($"spof:{top}", () => ComputeSpof(index, top));

    public IReadOnlyList<CouplingFinding> GetCoupling(DatabaseIndex index, int top = 20) =>
        index.GetOrAddAnalysis($"coupling:{top}", () => ComputeCoupling(index, top));

    public RedundancyReport GetRedundancies(DatabaseIndex index, int top = 20) =>
        index.GetOrAddAnalysis($"redundancy:{top}", () => ComputeRedundancy(index, top));

    public BottleneckReport GetBottlenecks(DatabaseIndex index, int top = 20) =>
        index.GetOrAddAnalysis($"bottlenecks:{top}", () => ComputeBottlenecks(index, top));

    public IReadOnlyList<BrokenReference> GetBrokenReferences(DatabaseIndex index, int top = 50) =>
        index.GetOrAddAnalysis($"broken:{top}", () => ComputeBrokenReferences(index, top));

    public OverviewReport GetOverview(DatabaseIndex index)
    {
        DatabaseSummary summary = index.GetSummary();
        IReadOnlyList<SpofFinding> spof = GetSinglePointsOfFailure(index, 10);
        IReadOnlyList<CouplingFinding> coupling = GetCoupling(index, 10);
        RedundancyReport redundancy = GetRedundancies(index, 10);
        BottleneckReport bottlenecks = GetBottlenecks(index, 10);
        IReadOnlyList<BrokenReference> broken = GetBrokenReferences(index, 10);

        List<string> warnings = new();
        if (summary.ClassificationCoverage < 0.999)
        {
            warnings.Add($"Solo el {summary.ClassificationCoverage:P0} de los módulos está clasificado (lee/escribe). Los cuellos de botella por escritura son parciales hasta ejecutar el escaneo profundo.");
        }
        if (summary.DynamicSqlCount > 0)
        {
            warnings.Add($"{summary.DynamicSqlCount} objeto(s) usan SQL dinámico: sus dependencias reales no son visibles.");
        }
        if (summary.BrokenReferenceCount > 0)
        {
            warnings.Add($"{summary.BrokenReferenceCount} referencia(s) a objetos que no existen.");
        }

        return new OverviewReport(summary, spof, coupling, redundancy, bottlenecks, broken, warnings);
    }

    // ---- Puntos únicos de fallo -----------------------------------------------------------

    private static List<SpofFinding> ComputeSpof(DatabaseIndex index, int top)
    {
        GraphSnapshot snapshot = index.Graph.Snapshot();
        HashSet<string> articulation = GraphAlgorithms.ArticulationPoints(snapshot, n => n.Kind is not NodeKind.Missing);

        IEnumerable<string> byFanIn = snapshot.Nodes.Values
            .Where(n => n.Kind is not NodeKind.Missing && snapshot.FanIn(n.Id) >= 2)
            .OrderByDescending(n => snapshot.FanIn(n.Id))
            .Take(SpofCandidatePool)
            .Select(n => n.Id);

        // Con miles de objetos hay muchos puntos de articulación triviales (hojas): se toman los más usados.
        IEnumerable<string> topArticulation = articulation
            .Where(id => snapshot.FanIn(id) >= 1)
            .OrderByDescending(snapshot.FanIn)
            .Take(SpofCandidatePool * 3);

        HashSet<string> candidates = new(byFanIn.Concat(topArticulation), StringComparer.OrdinalIgnoreCase);
        List<SpofFinding> findings = new();

        foreach (string id in candidates)
        {
            GraphNode node = snapshot.Nodes[id];
            int direct = snapshot.FanIn(id);
            int total = snapshot.GetDependents(id, 10).Count;
            if (total == 0)
            {
                continue;
            }

            int writers = snapshot.In(id).Count(e => e.Has(DependencyKind.Writes));
            bool isArticulation = articulation.Contains(id);

            List<string> reasons = new() { $"{total} objeto(s) dependen de él ({direct} directamente)" };
            if (isArticulation) reasons.Add("Punto de articulación: si falla, separa el grafo en partes desconectadas");
            if (writers >= 2) reasons.Add($"Escrito por {writers} procedimientos");
            if (node.IsCritical) reasons.Add("Coincide con un patrón de objeto crítico");
            if (node.IsExternal) reasons.Add("Dependencia externa (otra base o instancia)");

            double score = total * (node.IsCritical ? 1.5 : 1) * (isArticulation ? 1.25 : 1) + writers * 2;
            findings.Add(new SpofFinding(id, NodeLabels.For(node, index), node.Kind, direct, total, writers, isArticulation, node.IsCritical, Math.Round(score, 1), reasons));
        }

        return findings.OrderByDescending(f => f.Score).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Take(top).ToList();
    }

    // ---- Acoplamiento ---------------------------------------------------------------------

    private static List<CouplingFinding> ComputeCoupling(DatabaseIndex index, int top)
    {
        GraphSnapshot snapshot = index.Graph.Snapshot();
        return snapshot.Nodes.Values
            .Where(n => index.IsLocal(n) && n.Kind is not NodeKind.Missing)
            .Select(n =>
            {
                int fanIn = snapshot.FanIn(n.Id);
                int fanOut = snapshot.FanOut(n.Id);
                double instability = fanIn + fanOut == 0 ? 0 : fanOut / (double)(fanIn + fanOut);
                return new CouplingFinding(n.Id, NodeLabels.For(n, index), n.Kind, fanIn, fanOut, Math.Round(instability, 2), fanIn * fanOut);
            })
            .Where(f => f.FanIn + f.FanOut > 0)
            .OrderByDescending(f => f.FanIn + f.FanOut)
            .ThenByDescending(f => f.CouplingScore)
            .Take(top)
            .ToList();
    }

    // ---- Redundancias ---------------------------------------------------------------------

    private static RedundancyReport ComputeRedundancy(DatabaseIndex index, int top)
    {
        GraphSnapshot snapshot = index.Graph.Snapshot();
        List<GraphNode> modules = snapshot.Nodes.Values.Where(n => index.IsLocal(n) && n.IsProgrammable).ToList();

        // 1. Definiciones idénticas (misma huella SHA-256 calculada en el servidor).
        List<DuplicateGroup> duplicates = modules
            .Where(n => !string.IsNullOrEmpty(n.DefinitionHash))
            .GroupBy(n => n.DefinitionHash!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(
                g.Key,
                g.Select(n => n.Id).ToList(),
                g.Select(n => NodeLabels.For(n, index)).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(g => g.NodeIds.Count)
            .Take(top)
            .ToList();

        HashSet<string> duplicatedIds = new(duplicates.SelectMany(d => d.NodeIds), StringComparer.OrdinalIgnoreCase);

        // 2. SPs con conjuntos de dependencias muy parecidos (Jaccard), vía índice invertido para no comparar todos contra todos.
        Dictionary<string, HashSet<string>> dependencySets = modules
            .Where(n => n.Kind is NodeKind.StoredProcedure)
            .ToDictionary(
                n => n.Id,
                n => snapshot.Out(n.Id)
                    .Where(e => !e.Has(DependencyKind.TriggeredBy) && !e.Has(DependencyKind.SynonymFor))
                    .Select(e => e.TargetId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        Dictionary<string, List<string>> postings = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string procedure, HashSet<string> targets) in dependencySets)
        {
            if (targets.Count < MinDependenciesForSimilarity)
            {
                continue;
            }
            foreach (string target in targets)
            {
                if (!postings.TryGetValue(target, out List<string>? list))
                {
                    list = new List<string>();
                    postings[target] = list;
                }
                list.Add(procedure);
            }
        }

        Dictionary<(string, string), int> shared = new();
        int evaluations = 0;
        foreach (List<string> procedures in postings.Values.Where(p => p.Count is > 1 and <= MaxPostingList))
        {
            for (int i = 0; i < procedures.Count && evaluations < MaxPairEvaluations; i++)
            {
                for (int j = i + 1; j < procedures.Count && evaluations < MaxPairEvaluations; j++)
                {
                    (string, string) pair = string.CompareOrdinal(procedures[i], procedures[j]) < 0
                        ? (procedures[i], procedures[j])
                        : (procedures[j], procedures[i]);
                    shared[pair] = shared.TryGetValue(pair, out int count) ? count + 1 : 1;
                    evaluations++;
                }
            }
        }

        List<SimilarPair> similar = shared
            .Where(p => !(duplicatedIds.Contains(p.Key.Item1) && duplicatedIds.Contains(p.Key.Item2)))
            .Select(p =>
            {
                int a = dependencySets[p.Key.Item1].Count;
                int b = dependencySets[p.Key.Item2].Count;
                double jaccard = p.Value / (double)(a + b - p.Value);
                return (Pair: p.Key, Shared: p.Value, Jaccard: jaccard);
            })
            .Where(x => x.Jaccard >= SimilarityThreshold)
            .OrderByDescending(x => x.Jaccard)
            .ThenByDescending(x => x.Shared)
            .Take(top)
            .Select(x => new SimilarPair(
                x.Pair.Item1, NodeLabels.For(snapshot.Nodes[x.Pair.Item1], index),
                x.Pair.Item2, NodeLabels.For(snapshot.Nodes[x.Pair.Item2], index),
                Math.Round(x.Jaccard, 2), x.Shared))
            .ToList();

        return new RedundancyReport(duplicates, similar);
    }

    // ---- Cuellos de botella ---------------------------------------------------------------

    private static BottleneckReport ComputeBottlenecks(DatabaseIndex index, int top)
    {
        GraphSnapshot snapshot = index.Graph.Snapshot();
        DatabaseSummary summary = index.GetSummary();

        // 1. Tablas escritas por muchos SPs: contención de bloqueos.
        List<WriteHotspot> hotspots = snapshot.Nodes.Values
            .Where(n => n.Kind is NodeKind.Table or NodeKind.External or NodeKind.Synonym)
            .Select(n =>
            {
                List<GraphEdge> writers = snapshot.In(n.Id).Where(e => e.Has(DependencyKind.Writes)).ToList();
                int readers = snapshot.In(n.Id).Count(e => e.Has(DependencyKind.Reads));
                return new WriteHotspot(
                    n.Id,
                    NodeLabels.For(n, index),
                    writers.Count,
                    readers,
                    writers.Select(w => NodeLabels.For(snapshot.Nodes[w.SourceId], index)).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Take(15).ToList());
            })
            .Where(h => h.Writers >= 2)
            .OrderByDescending(h => h.Writers)
            .ThenByDescending(h => h.Readers)
            .Take(top)
            .ToList();

        // 2. Ciclos de ejecución (A → B → A).
        static bool IsExecution(GraphEdge e) => e.Has(DependencyKind.Executes) || e.Has(DependencyKind.Calls);

        List<List<string>> components = GraphAlgorithms.StronglyConnectedComponents(snapshot, IsExecution);
        List<CycleFinding> cycles = components
            .Where(c => c.Count > 1)
            .OrderByDescending(c => c.Count)
            .Take(top)
            .Select(c => new CycleFinding(c, c.Select(id => NodeLabels.For(snapshot.Nodes[id], index)).ToList()))
            .ToList();

        // 3. Cadenas de EXEC anidados muy profundas (transacciones largas, difícil de depurar).
        HashSet<string> cyclicNodes = new(components.Where(c => c.Count > 1).SelectMany(c => c), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (int Depth, string? Next)> chains = GraphAlgorithms.LongestChains(snapshot, IsExecution, cyclicNodes);
        HashSet<string> calledByOthers = new(
            snapshot.Nodes.Keys.Where(id => snapshot.In(id).Any(IsExecution)),
            StringComparer.OrdinalIgnoreCase);

        List<CallChain> deepChains = chains
            .Where(c => c.Value.Depth >= DeepChainThreshold && !calledByOthers.Contains(c.Key))
            .OrderByDescending(c => c.Value.Depth)
            .Take(top)
            .Select(c =>
            {
                List<string> path = new() { NodeLabels.For(snapshot.Nodes[c.Key], index) };
                string? next = c.Value.Next;
                while (next is not null && path.Count <= 50)
                {
                    path.Add(NodeLabels.For(snapshot.Nodes[next], index));
                    next = chains.TryGetValue(next, out (int Depth, string? Next) step) ? step.Next : null;
                }
                return new CallChain(c.Key, path[0], c.Value.Depth, path);
            })
            .ToList();

        // 4. SQL dinámico: opaco para el optimizador y para este análisis.
        List<ObjectMetric> dynamicSql = snapshot.Nodes.Values
            .Where(n => index.IsLocal(n) && n.HasDynamicSql)
            .Select(n => new ObjectMetric(n.Id, NodeLabels.For(n, index), n.Kind, snapshot.FanIn(n.Id)))
            .OrderByDescending(m => m.Value)
            .Take(top)
            .ToList();

        // 5. "Procedimientos dios": tocan demasiados objetos.
        List<ObjectMetric> godProcedures = snapshot.Nodes.Values
            .Where(n => index.IsLocal(n) && n.Kind is NodeKind.StoredProcedure && snapshot.FanOut(n.Id) >= GodProcedureFanOut)
            .Select(n => new ObjectMetric(n.Id, NodeLabels.For(n, index), n.Kind, snapshot.FanOut(n.Id)))
            .OrderByDescending(m => m.Value)
            .Take(top)
            .ToList();

        return new BottleneckReport(hotspots, deepChains, cycles, dynamicSql, godProcedures, Math.Round(summary.ClassificationCoverage, 3));
    }

    // ---- Referencias rotas ----------------------------------------------------------------

    private static List<BrokenReference> ComputeBrokenReferences(DatabaseIndex index, int top)
    {
        GraphSnapshot snapshot = index.Graph.Snapshot();
        return snapshot.Nodes.Values
            .Where(n => n.Kind is NodeKind.Missing)
            .Select(n =>
            {
                List<GraphNode> referrers = snapshot.In(n.Id).Select(e => snapshot.Nodes[e.SourceId]).ToList();
                return new BrokenReference(
                    n.Id,
                    n.DisplayName,
                    referrers.Select(r => r.Id).ToList(),
                    referrers.Select(r => NodeLabels.For(r, index)).ToList());
            })
            .OrderByDescending(b => b.ReferencedByIds.Count)
            .ThenBy(b => b.MissingName, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .ToList();
    }
}
