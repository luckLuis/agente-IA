using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Catalog;
using SqlGraphCopilot.Application.Parsing;
using SqlGraphCopilot.Domain;
using SqlGraphCopilot.Domain.Indexing;

namespace SqlGraphCopilot.Application.Indexing;

/// <summary>
/// El catálogo dice QUÉ referencia un objeto, pero no CÓMO (lee, escribe, ejecuta).
/// El clasificador lee la definición, la analiza con ScriptDom y etiqueta las aristas existentes.
/// El catálogo manda sobre la existencia; el parser solo aporta el tipo de relación.
/// Se ejecuta bajo demanda (al expandir un nodo) o completo con el escaneo profundo.
/// </summary>
public sealed class DependencyClassifier
{
    private const int DeepScanBatchSize = 200;
    private const DependencyKind StructuralKinds = DependencyKind.TriggeredBy | DependencyKind.SynonymFor;

    private readonly ICatalogReader _reader;
    private readonly ITSqlParsingService _parser;
    private readonly IInstanceRegistry _registry;
    private readonly ILogger<DependencyClassifier> _logger;

    public DependencyClassifier(
        ICatalogReader reader,
        ITSqlParsingService parser,
        IInstanceRegistry registry,
        ILogger<DependencyClassifier> logger)
    {
        _reader = reader;
        _parser = parser;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>Clasifica un nodo si aún no lo está. Devuelve true si cambió algo.</summary>
    public async Task<bool> EnsureClassifiedAsync(DatabaseIndex index, string nodeId, CancellationToken cancellationToken)
    {
        GraphNode? node = index.Graph.GetNode(nodeId);
        if (node is null || node.IsClassified || !node.IsProgrammable || !index.IsLocal(node) || node.ObjectId is null)
        {
            return false;
        }

        string? definition = await _reader.ReadDefinitionAsync(index.Instance, index.Database, node.ObjectId.Value, cancellationToken);
        if (definition is null)
        {
            // Módulo cifrado (WITH ENCRYPTION): no hay más que aprender.
            index.Graph.UpdateNode(node.Id, n => n with { IsClassified = true });
            index.MarkChanged();
            return true;
        }

        SqlAnalysisResult result = _parser.ParseSqlScript(definition);
        Apply(index, node, result);
        index.MarkChanged();
        return true;
    }

    public async Task<int> EnsureClassifiedAsync(DatabaseIndex index, IEnumerable<string> nodeIds, int limit, CancellationToken cancellationToken)
    {
        int classified = 0;
        foreach (string nodeId in nodeIds)
        {
            if (classified >= limit)
            {
                break;
            }
            if (await EnsureClassifiedAsync(index, nodeId, cancellationToken))
            {
                classified++;
            }
        }
        return classified;
    }

    /// <summary>Clasifica todos los módulos de la base (lectura en streaming y parseo en paralelo).</summary>
    public async Task<DeepScanResult> DeepScanAsync(DatabaseIndex index, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int total = 0;
        int withErrors = 0;
        ConcurrentBag<string> errorSamples = new();
        List<ObjectDefinition> batch = new(DeepScanBatchSize);

        await foreach (ObjectDefinition definition in _reader.ReadDefinitionsAsync(index.Instance, index.Database, cancellationToken))
        {
            batch.Add(definition);
            if (batch.Count >= DeepScanBatchSize)
            {
                ProcessBatch(batch);
                batch.Clear();
            }
        }
        ProcessBatch(batch);

        index.MarkDeepScanCompleted();
        DatabaseSummary summary = index.GetSummary();

        _logger.LogInformation(
            "Escaneo profundo {Instance}/{Database}: {Total} módulos, {Errors} con errores de parseo, {Elapsed} ms",
            index.Instance, index.Database, total, withErrors, stopwatch.ElapsedMilliseconds);

        return new DeepScanResult(total, withErrors, stopwatch.ElapsedMilliseconds, summary.ClassificationCoverage, errorSamples.Take(10).ToList());

        void ProcessBatch(List<ObjectDefinition> items)
        {
            ParallelOptions parallel = new() { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount };
            Parallel.ForEach(items, parallel, item =>
            {
                string? nodeId = index.GetNodeIdByObjectId(item.ObjectId);
                GraphNode? node = nodeId is null ? null : index.Graph.GetNode(nodeId);
                if (node is null)
                {
                    return;
                }

                Interlocked.Increment(ref total);
                SqlAnalysisResult result;
                try
                {
                    result = _parser.ParseSqlScript(item.Definition);
                }
                catch (Exception ex)
                {
                    // Un módulo problemático no detiene el escaneo de miles de objetos.
                    Interlocked.Increment(ref withErrors);
                    errorSamples.Add($"{node.DisplayName}: {ex.Message}");
                    return;
                }
                if (result.HasErrors)
                {
                    Interlocked.Increment(ref withErrors);
                    if (errorSamples.Count < 10)
                    {
                        errorSamples.Add($"{node.DisplayName}: {result.Errors[0]}");
                    }
                }
                Apply(index, node, result);
            });
        }
    }

    /// <summary>Etiqueta las aristas salientes del nodo con lo que encontró el parser.</summary>
    internal void Apply(DatabaseIndex index, GraphNode node, SqlAnalysisResult result)
    {
        foreach (GraphEdge edge in index.Graph.GetOutgoing(node.Id))
        {
            GraphNode? target = index.Graph.GetNode(edge.TargetId);
            if (target is null || target.Kind is NodeKind.LinkedServer)
            {
                continue;
            }

            DependencyKind detected = DependencyKind.None;
            if (MatchesAny(result.Writes, target.Key)) detected |= DependencyKind.Writes;
            if (MatchesAny(result.Reads, target.Key)) detected |= DependencyKind.Reads;
            if (MatchesAny(result.Executes, target.Key)) detected |= DependencyKind.Executes;
            if (MatchesAny(result.Calls, target.Key)) detected |= DependencyKind.Calls;

            if (detected == DependencyKind.None)
            {
                continue; // El parser no la vio (p. ej. referencia en SQL dinámico): se conserva la del catálogo.
            }

            DependencyKind newKinds = detected | (edge.Kinds & StructuralKinds);
            if (newKinds != edge.Kinds)
            {
                index.Graph.SetEdgeKinds(edge.SourceId, edge.TargetId, newKinds);
            }
        }

        // OPENQUERY / EXEC ... AT: el catálogo no registra estas dependencias; se representa el servidor vinculado.
        if (result.UsesOpenQuery)
        {
            foreach (string linkedServer in result.LinkedServers)
            {
                if (HasExternalEdgeTo(index, node.Id, linkedServer))
                {
                    continue;
                }
                string serverName = _registry.ResolveServer(linkedServer) ?? linkedServer;
                ObjectKey key = new(serverName, "*", "*", linkedServer);
                GraphNode linkedNode = index.Graph.GetOrAddNode(new GraphNode
                {
                    Key = key,
                    Kind = NodeKind.LinkedServer,
                    Scope = ExternalScope.LinkedServer
                });
                index.Graph.AddEdge(node.Id, linkedNode.Id, DependencyKind.References);
            }
        }

        index.Graph.UpdateNode(node.Id, n => n with
        {
            IsClassified = true,
            HasDynamicSql = result.HasDynamicSql,
            UsesOpenQuery = result.UsesOpenQuery
        });
    }

    private bool HasExternalEdgeTo(DatabaseIndex index, string nodeId, string linkedServer)
    {
        string resolved = _registry.ResolveServer(linkedServer) ?? linkedServer;
        return index.Graph.GetOutgoing(nodeId)
            .Select(e => index.Graph.GetNode(e.TargetId))
            .Any(t => t is not null && t.Kind is not NodeKind.LinkedServer &&
                      t.Key.Server.Equals(resolved, StringComparison.OrdinalIgnoreCase) &&
                      !t.Key.Server.Equals(index.Instance, StringComparison.OrdinalIgnoreCase));
    }

    private bool MatchesAny(IReadOnlyList<SqlObjectName> names, ObjectKey target)
    {
        foreach (SqlObjectName name in names)
        {
            if (!name.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (name.Schema is not null && !name.Schema.Equals(target.Schema, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (name.Database is not null && !name.Database.Equals(target.Database, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (name.Server is not null)
            {
                string resolved = _registry.ResolveServer(name.Server) ?? name.Server;
                if (!resolved.Equals(target.Server, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            return true;
        }
        return false;
    }
}

public sealed record DeepScanResult(int ModulesParsed, int ModulesWithErrors, long ElapsedMilliseconds, double ClassificationCoverage, IReadOnlyList<string> ErrorSamples);
