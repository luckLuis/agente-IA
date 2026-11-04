using Microsoft.Extensions.Logging;
using SqlGraphCopilot.Core.Agent.Llm;
using SqlGraphCopilot.Core.Analysis;
using SqlGraphCopilot.Core.Domain;
using SqlGraphCopilot.Core.Graph;
using SqlGraphCopilot.Core.Indexing;
using SqlGraphCopilot.Core.Metadata;
using SqlGraphCopilot.Core.Usage;

namespace SqlGraphCopilot.Core.Agent;

/// <summary>
/// Orquestador del agente: Razonar (intención + objetos) → Actuar (análisis sobre el grafo real)
/// → Observar (hechos estructurados) → Sintetizar (LLM si está configurado; si no, narrador determinista).
/// Para ExplainLogic y MigrateToCode además lee el T-SQL real del objeto desde el catálogo.
/// Ahorro de tokens: el grafo responde sin LLM cuando puede y las respuestas del LLM se guardan en caché por hash del prompt.
/// </summary>
public sealed class SqlCopilotAgent
{
    private const int MaxFocusNodes = 40;
    private const int MaxFactsItems = 25;

    // Límites para explicar / migrar código
    private const int MaxCallees = 3;
    private const int MaxDefinitionChars = 60_000;
    private const int MaxCalleeChars = 8_000;

    private readonly IDatabaseIndexProvider _indexes;
    private readonly DependencyClassifier _classifier;
    private readonly ImpactAnalyzer _impactAnalyzer;
    private readonly StructuralAnalyzer _structuralAnalyzer;
    private readonly IntentClassifier _intentClassifier;
    private readonly EntityResolver _entityResolver;
    private readonly DeterministicNarrator _narrator;
    private readonly PromptBuilder _promptBuilder;
    private readonly ILlmClient _llm;
    private readonly ICatalogReader _catalog;
    private readonly ILogger<SqlCopilotAgent> _logger;
    private readonly LlmResponseCache? _cache;

    // La caché es opcional (último parámetro, null por defecto): TestEngine y otros constructores directos siguen compilando.
    public SqlCopilotAgent(
        IDatabaseIndexProvider indexes,
        DependencyClassifier classifier,
        ImpactAnalyzer impactAnalyzer,
        StructuralAnalyzer structuralAnalyzer,
        IntentClassifier intentClassifier,
        EntityResolver entityResolver,
        DeterministicNarrator narrator,
        PromptBuilder promptBuilder,
        ILlmClient llm,
        ICatalogReader catalog,
        ILogger<SqlCopilotAgent> logger,
        LlmResponseCache? cache = null)
    {
        _indexes = indexes;
        _classifier = classifier;
        _impactAnalyzer = impactAnalyzer;
        _structuralAnalyzer = structuralAnalyzer;
        _intentClassifier = intentClassifier;
        _entityResolver = entityResolver;
        _narrator = narrator;
        _promptBuilder = promptBuilder;
        _llm = llm;
        _catalog = catalog;
        _logger = logger;
        _cache = cache;
    }

    public async Task<AgentResponse> AskAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        DatabaseIndex index = await _indexes.GetAsync(request.Instance, request.Database, cancellationToken);

        // 1. Razonar
        AgentIntent? detected = _intentClassifier.Classify(request.Message);
        EntityResolution entities = _entityResolver.Resolve(index, request.Message, request.FocusNodeId);
        AgentIntent intent = detected ?? (entities.Matches.Count > 0 ? AgentIntent.Impact : AgentIntent.Overview);

        _logger.LogInformation(
            "[AGENTE] Intención={Intent} Objetos={Entities} Foco={UsedFocus} Base={Instance}/{Database}",
            intent, string.Join(", ", entities.Matches.Select(m => m.DisplayName)), entities.UsedFocus, index.Instance, index.Database);

        // Explicar lógica / migrar a código: necesita el T-SQL real, flujo aparte.
        if (intent is AgentIntent.ExplainLogic or AgentIntent.MigrateToCode)
        {
            return await AskAboutCodeAsync(index, intent, entities, request.Message, cancellationToken);
        }

        // 2. Actuar + 3. Observar
        Outcome outcome = intent switch
        {
            AgentIntent.Impact or AgentIntent.Explain => await AnalyzeObjectAsync(index, intent, entities, cancellationToken),
            AgentIntent.SinglePointsOfFailure => SinglePointsOfFailure(index),
            AgentIntent.Coupling => Coupling(index),
            AgentIntent.Redundancy => Redundancy(index),
            AgentIntent.Bottlenecks => Bottlenecks(index),
            AgentIntent.BrokenReferences => BrokenReferences(index),
            AgentIntent.Help => new Outcome(_narrator.Help(), null, Array.Empty<string>(), Array.Empty<string>()),
            _ => Overview(index)
        };

        // 4. Sintetizar
        if (outcome.Facts is null || !_llm.IsEnabled)
        {
            return new AgentResponse(outcome.DeterministicReply, intent, outcome.FocusNodeIds, outcome.Facts, "deterministic", outcome.Warnings);
        }

        try
        {
            LlmPromptParts prompt = _promptBuilder.Build(request.Message, intent, index.Instance, index.Database, outcome.Facts);
            (string reply, bool cached) = await CompleteAsync(prompt, request.Message, cancellationToken);
            if (!string.IsNullOrWhiteSpace(reply))
            {
                return new AgentResponse(reply.Trim(), intent, outcome.FocusNodeIds, outcome.Facts, EngineName(cached), outcome.Warnings);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "[AGENTE] El LLM falló; se usa el narrador determinista.");
        }

        string fallback = outcome.DeterministicReply + "\n\n_(Respuesta generada sin LLM: el proveedor no respondió.)_";
        return new AgentResponse(fallback, intent, outcome.FocusNodeIds, outcome.Facts, "deterministic-fallback", outcome.Warnings);
    }

    /// <summary>
    /// Llama al LLM, o devuelve la respuesta guardada si el prompt (reglas + hechos + código + pregunta) es idéntico a uno anterior.
    /// Las peticiones de regenerar ("de nuevo", "otra vez"...) saltan la caché.
    /// </summary>
    private async Task<(string Reply, bool Cached)> CompleteAsync(LlmPromptParts prompt, string question, CancellationToken cancellationToken)
    {
        string? key = _cache is null || WantsFreshAnswer(question) ? null : LlmResponseCache.KeyFor(prompt.System, prompt.User);

        if (key is not null && _cache!.TryGet(key, out string? stored) && !string.IsNullOrWhiteSpace(stored))
        {
            _logger.LogInformation("[AGENTE] Respuesta servida desde la caché (sin llamar al LLM).");
            if (UsageContext.Current is { } usage) usage.CacheHit = true;
            return (stored, true);
        }

        string reply = await _llm.CompleteAsync(new LlmPrompt(prompt.System, prompt.User), cancellationToken);
        if (key is not null && !string.IsNullOrWhiteSpace(reply))
        {
            _cache!.Set(key, reply);
        }
        return (reply, false);
    }

    private string EngineName(bool cached) => cached ? $"{_llm.Name} (caché)" : _llm.Name;

    private static bool WantsFreshAnswer(string question)
    {
        string n = IntentClassifier.Normalize(question);
        return n.Contains("de nuevo", StringComparison.Ordinal)
            || n.Contains("otra vez", StringComparison.Ordinal)
            || n.Contains("nuevamente", StringComparison.Ordinal)
            || n.Contains("regener", StringComparison.Ordinal);
    }

    /// <summary>
    /// Explica la lógica o migra a C# un SP/función/trigger/vista leyendo su T-SQL real.
    /// El código viaja solo al proveedor LLM; no se escribe en el log.
    /// </summary>
    private async Task<AgentResponse> AskAboutCodeAsync(
        DatabaseIndex index,
        AgentIntent intent,
        EntityResolution entities,
        string question,
        CancellationToken cancellationToken)
    {
        GraphNode? target = entities.Matches.FirstOrDefault(n => index.IsLocal(n) && n.IsProgrammable && n.ObjectId is not null);
        if (target is null)
        {
            IEnumerable<string> suggestions = index.Graph.GetNodes()
                .Where(n => index.IsLocal(n) && n.Kind is NodeKind.StoredProcedure)
                .OrderByDescending(n => index.Graph.FanIn(n.Id))
                .Take(5)
                .Select(n => n.DisplayName);
            return new AgentResponse(_narrator.AskForObject(suggestions), intent, Array.Empty<string>(), null, "deterministic", Array.Empty<string>());
        }

        string? definition = await _catalog.ReadDefinitionAsync(index.Instance, index.Database, target.ObjectId!.Value, cancellationToken);
        if (string.IsNullOrWhiteSpace(definition))
        {
            return new AgentResponse(
                $"No puedo leer el código de `{target.DisplayName}`: está cifrado (WITH ENCRYPTION) o la cuenta no tiene VIEW DEFINITION.",
                intent, new[] { target.Id }, null, "deterministic", Array.Empty<string>());
        }

        List<string> warnings = new();
        if (definition.Length > MaxDefinitionChars)
        {
            definition = definition[..MaxDefinitionChars];
            warnings.Add($"La definición superó {MaxDefinitionChars:N0} caracteres y se truncó: el resultado puede estar incompleto.");
        }

        // Relaciones lee/escribe/ejecuta del objeto (contexto para el LLM).
        await _classifier.EnsureClassifiedAsync(index, new[] { target.Id }, 1, cancellationToken);

        // SPs/funciones que llama: su código ayuda a migrar la lógica completa.
        List<GraphNode> calleeNodes = new();
        foreach (GraphEdge edge in index.Graph.GetOutgoing(target.Id))
        {
            GraphNode? node = index.Graph.GetNode(edge.TargetId);
            if (node is not null
                && index.IsLocal(node)
                && node.ObjectId is not null
                && (node.Kind is NodeKind.StoredProcedure || node.Kind is NodeKind.Function)
                && calleeNodes.All(c => !string.Equals(c.Id, node.Id, StringComparison.OrdinalIgnoreCase)))
            {
                calleeNodes.Add(node);
                if (calleeNodes.Count >= MaxCallees)
                {
                    break;
                }
            }
        }

        List<CodeContext> callees = new();
        foreach (GraphNode callee in calleeNodes)
        {
            string? code = await _catalog.ReadDefinitionAsync(index.Instance, index.Database, callee.ObjectId!.Value, cancellationToken);
            if (!string.IsNullOrWhiteSpace(code))
            {
                callees.Add(new CodeContext(callee.DisplayName, code.Length > MaxCalleeChars ? code[..MaxCalleeChars] : code));
            }
        }

        object facts = new
        {
            target = target.DisplayName,
            kind = target.Kind,
            hasDynamicSql = target.HasDynamicSql,
            uses = index.Graph.GetOutgoing(target.Id).Take(MaxFactsItems).Select(e => new
            {
                name = index.Graph.GetNode(e.TargetId)?.DisplayName,
                relation = DeterministicNarrator.Relation(e.Kinds)
            }).ToList(),
            usedBy = index.Graph.GetIncoming(target.Id).Take(MaxFactsItems)
                .Select(e => index.Graph.GetNode(e.SourceId)?.DisplayName).ToList()
        };
        string[] focus = { target.Id };

        if (target.HasDynamicSql)
        {
            warnings.Add("Usa SQL dinámico: sus dependencias reales y parte de la lógica pueden no ser visibles.");
        }

        string codeBlock = "```sql\n" + definition + "\n```";

        if (!_llm.IsEnabled)
        {
            return new AgentResponse(
                $"No hay un LLM configurado, así que no puedo explicar ni migrar `{target.DisplayName}`. Este es su código actual:\n\n{codeBlock}",
                intent, focus, facts, "deterministic", warnings);
        }

        try
        {
            LlmPromptParts prompt = _promptBuilder.BuildCode(
                question, intent, index.Instance, index.Database, target.DisplayName, definition, callees, facts);
            (string reply, bool cached) = await CompleteAsync(prompt, question, cancellationToken);
            if (!string.IsNullOrWhiteSpace(reply))
            {
                return new AgentResponse(reply.Trim(), intent, focus, facts, EngineName(cached), warnings);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "[AGENTE] El LLM falló en explicar/migrar código.");
        }

        return new AgentResponse(
            $"El proveedor LLM no respondió. Este es el código de `{target.DisplayName}`:\n\n{codeBlock}",
            intent, focus, facts, "deterministic-fallback", warnings);
    }

    private async Task<Outcome> AnalyzeObjectAsync(DatabaseIndex index, AgentIntent intent, EntityResolution entities, CancellationToken cancellationToken)
    {
        if (entities.Matches.Count == 0)
        {
            IEnumerable<string> suggestions = index.Graph.GetNodes()
                .Where(n => index.IsLocal(n) && n.Kind is not NodeKind.Missing)
                .OrderByDescending(n => index.Graph.FanIn(n.Id))
                .Take(5)
                .Select(n => n.DisplayName);
            return new Outcome(_narrator.AskForObject(suggestions), null, Array.Empty<string>(), Array.Empty<string>());
        }

        GraphNode target = entities.Matches[0];

        // Clasifica el objeto y quienes dependen de él directamente para que el reporte diga lee/escribe/ejecuta.
        IEnumerable<string> toClassify = new[] { target.Id }
            .Concat(index.Graph.GetIncoming(target.Id).Select(e => e.SourceId));
        await _classifier.EnsureClassifiedAsync(index, toClassify, 25, cancellationToken);

        ImpactReport impact = _impactAnalyzer.Analyze(index, target.Id, ImpactAnalyzer.DefaultMaxDepth, _indexes.GetLoaded());

        List<string> focus = new List<string> { target.Id }
            .Concat(impact.Affected.Where(a => !a.IsOtherDatabase).Select(a => a.NodeId))
            .Concat(intent is AgentIntent.Explain ? index.Graph.GetOutgoing(target.Id).Select(e => e.TargetId) : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxFocusNodes)
            .ToList();

        object facts = new
        {
            target = new
            {
                name = impact.TargetName,
                kind = target.Kind,
                database = target.Key.Database,
                isCritical = target.IsCritical,
                hasDynamicSql = target.HasDynamicSql,
                fanIn = index.Graph.FanIn(target.Id),
                fanOut = index.Graph.FanOut(target.Id)
            },
            otherMatches = entities.Matches.Skip(1).Select(m => m.DisplayName).ToList(),
            riskLevel = impact.Level,
            score = impact.Score,
            totalAffected = impact.TotalAffected,
            criticalAffected = impact.CriticalAffected,
            affected = impact.Affected.Take(MaxFactsItems).Select(a => new
            {
                name = a.Name,
                kind = a.Kind,
                depth = a.Depth,
                relation = DeterministicNarrator.Relation(a.Relation),
                critical = a.IsCritical,
                otherDatabase = a.IsOtherDatabase
            }).ToList(),
            uses = intent is AgentIntent.Explain
                ? index.Graph.GetOutgoing(target.Id).Take(MaxFactsItems).Select(e => new
                {
                    name = index.Graph.GetNode(e.TargetId)?.DisplayName,
                    relation = DeterministicNarrator.Relation(e.Kinds)
                }).ToList()
                : null,
            dataImpact = impact.DataImpact.Take(10).Select(d => new { table = d.TableName, readers = d.ReaderNames.Take(10) }).ToList(),
            searchedDatabases = impact.SearchedDatabases,
            warnings = impact.Warnings
        };

        string reply = intent is AgentIntent.Explain ? _narrator.Explain(target, index, impact) : _narrator.Impact(impact);
        if (entities.Matches.Count > 1)
        {
            reply += $"\n\n_También mencionó: {string.Join(", ", entities.Matches.Skip(1).Select(m => $"`{m.DisplayName}`"))}. Pregunte por cada uno para su análisis._";
        }

        return new Outcome(reply, facts, focus, impact.Warnings);
    }

    private Outcome SinglePointsOfFailure(DatabaseIndex index)
    {
        IReadOnlyList<SpofFinding> findings = _structuralAnalyzer.GetSinglePointsOfFailure(index, 15);
        object facts = new { singlePointsOfFailure = findings.Take(MaxFactsItems).Select(f => new { f.Name, f.Kind, f.DirectDependents, f.TotalDependents, f.Writers, f.IsArticulationPoint, f.IsCritical, f.Reasons }) };
        return new Outcome(_narrator.SinglePointsOfFailure(findings), facts, findings.Select(f => f.NodeId).ToList(), Array.Empty<string>());
    }

    private Outcome Coupling(DatabaseIndex index)
    {
        IReadOnlyList<CouplingFinding> findings = _structuralAnalyzer.GetCoupling(index, 15);
        object facts = new { coupling = findings.Select(f => new { f.Name, f.Kind, f.FanIn, f.FanOut, f.Instability }) };
        return new Outcome(_narrator.Coupling(findings), facts, findings.Select(f => f.NodeId).ToList(), Array.Empty<string>());
    }

    private Outcome Redundancy(DatabaseIndex index)
    {
        RedundancyReport report = _structuralAnalyzer.GetRedundancies(index, 15);
        object facts = new
        {
            exactDuplicates = report.ExactDuplicates.Select(g => g.Names),
            similarProcedures = report.SimilarProcedures.Select(p => new { p.FirstName, p.SecondName, p.Similarity, p.SharedDependencies })
        };
        List<string> focus = report.ExactDuplicates.SelectMany(g => g.NodeIds)
            .Concat(report.SimilarProcedures.SelectMany(p => new[] { p.FirstId, p.SecondId }))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFocusNodes).ToList();
        return new Outcome(_narrator.Redundancy(report), facts, focus, Array.Empty<string>());
    }

    private Outcome Bottlenecks(DatabaseIndex index)
    {
        BottleneckReport report = _structuralAnalyzer.GetBottlenecks(index, 15);
        List<string> warnings = report.ClassificationCoverage < 0.999
            ? new List<string> { $"Cobertura de clasificación {report.ClassificationCoverage:P0}: ejecute el escaneo profundo." }
            : new List<string>();
        object facts = new
        {
            writeHotspots = report.WriteHotspots.Select(h => new { h.TableName, h.Writers, h.Readers, writers = h.WriterNames.Take(8) }),
            deepCallChains = report.DeepCallChains.Select(c => new { c.Depth, c.Path }),
            cycles = report.Cycles.Select(c => c.Names),
            godProcedures = report.GodProcedures.Select(g => new { g.Name, objectsTouched = g.Value }),
            dynamicSql = report.DynamicSqlObjects.Select(d => d.Name),
            classificationCoverage = report.ClassificationCoverage,
            warnings
        };
        List<string> focus = report.WriteHotspots.Select(h => h.TableId)
            .Concat(report.Cycles.SelectMany(c => c.NodeIds))
            .Concat(report.GodProcedures.Select(g => g.NodeId))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFocusNodes).ToList();
        return new Outcome(_narrator.Bottlenecks(report), facts, focus, warnings);
    }

    private Outcome BrokenReferences(DatabaseIndex index)
    {
        IReadOnlyList<BrokenReference> broken = _structuralAnalyzer.GetBrokenReferences(index, 30);
        object facts = new { brokenReferences = broken.Select(b => new { missing = b.MissingName, referencedBy = b.ReferencedByNames.Take(10) }) };
        List<string> focus = broken.SelectMany(b => b.ReferencedByIds.Prepend(b.MissingId)).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFocusNodes).ToList();
        return new Outcome(_narrator.BrokenReferences(broken), facts, focus, Array.Empty<string>());
    }

    private Outcome Overview(DatabaseIndex index)
    {
        OverviewReport report = _structuralAnalyzer.GetOverview(index);
        DatabaseSummary s = report.Summary;
        object facts = new
        {
            summary = new
            {
                objectCounts = s.ObjectCounts,
                s.EdgeCount,
                s.ExternalReferenceCount,
                s.LinkedServerCount,
                s.BrokenReferenceCount,
                s.ClassificationCoverage,
                s.DynamicSqlCount
            },
            singlePointsOfFailure = report.SinglePointsOfFailure.Select(f => new { f.Name, f.TotalDependents, f.Reasons }),
            mostCoupled = report.Coupling.Select(c => new { c.Name, c.FanIn, c.FanOut }),
            writeHotspots = report.Bottlenecks.WriteHotspots.Select(h => new { h.TableName, h.Writers }),
            cycles = report.Bottlenecks.Cycles.Select(c => c.Names),
            duplicates = report.Redundancy.ExactDuplicates.Select(d => d.Names),
            similarProcedures = report.Redundancy.SimilarProcedures.Select(p => new { p.FirstName, p.SecondName, p.Similarity }),
            brokenReferences = report.BrokenReferences.Select(b => b.MissingName),
            warnings = report.Warnings
        };
        List<string> focus = report.SinglePointsOfFailure.Select(f => f.NodeId).Take(MaxFocusNodes).ToList();
        return new Outcome(_narrator.Overview(report), facts, focus, report.Warnings);
    }

    private sealed record Outcome(string DeterministicReply, object? Facts, IReadOnlyList<string> FocusNodeIds, IReadOnlyList<string> Warnings);
}
