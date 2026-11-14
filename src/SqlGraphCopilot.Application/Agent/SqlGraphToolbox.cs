using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Analysis;
using SqlGraphCopilot.Application.Catalog;
using SqlGraphCopilot.Application.Exploration;
using SqlGraphCopilot.Application.Indexing;
using SqlGraphCopilot.Domain;
using SqlGraphCopilot.Domain.Indexing;

namespace SqlGraphCopilot.Application.Agent;

/// <summary>
/// Punto de integración con el agente del core (base común NuGet): expone las capacidades
/// del grafo como "herramientas" que otro agente puede invocar, por ejemplo cuando el análisis
/// de una HU señala un SP sospechoso ("este SP está mal, revisa") y necesita su radio de impacto.
/// Todas las respuestas son metadata: nombres, tipos, relaciones y métricas. Nunca código.
/// </summary>
public interface ISqlGraphToolbox
{
    Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(string instance, CancellationToken cancellationToken);
    Task<PagedResult<GraphNode>> FindObjectsAsync(string instance, string database, string text, int page, CancellationToken cancellationToken);
    Task<ExplorationResult> GetNeighborhoodAsync(string nodeId, int depth, TraversalDirection direction, CancellationToken cancellationToken);
    Task<ImpactReport> AnalyzeImpactAsync(string nodeId, int maxDepth, CancellationToken cancellationToken);
    Task<OverviewReport> GetOverviewAsync(string instance, string database, CancellationToken cancellationToken);
    Task<AgentResponse> AskAsync(AgentRequest request, CancellationToken cancellationToken);
}

public sealed class SqlGraphToolbox : ISqlGraphToolbox
{
    private readonly ICatalogReader _catalog;
    private readonly IDatabaseIndexProvider _indexes;
    private readonly GraphExplorer _explorer;
    private readonly ImpactAnalyzer _impact;
    private readonly StructuralAnalyzer _structural;
    private readonly SqlCopilotAgent _agent;

    public SqlGraphToolbox(
        ICatalogReader catalog,
        IDatabaseIndexProvider indexes,
        GraphExplorer explorer,
        ImpactAnalyzer impact,
        StructuralAnalyzer structural,
        SqlCopilotAgent agent)
    {
        _catalog = catalog;
        _indexes = indexes;
        _explorer = explorer;
        _impact = impact;
        _structural = structural;
        _agent = agent;
    }

    public Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(string instance, CancellationToken cancellationToken) =>
        _catalog.ListDatabasesAsync(instance, cancellationToken);

    public async Task<PagedResult<GraphNode>> FindObjectsAsync(string instance, string database, string text, int page, CancellationToken cancellationToken)
    {
        DatabaseIndex index = await _indexes.GetAsync(instance, database, cancellationToken);
        return index.Search(new ObjectSearchQuery(text, null, true, page, 25));
    }

    public Task<ExplorationResult> GetNeighborhoodAsync(string nodeId, int depth, TraversalDirection direction, CancellationToken cancellationToken) =>
        _explorer.GetNeighborhoodAsync(nodeId, depth, direction, null, cancellationToken);

    public Task<ImpactReport> AnalyzeImpactAsync(string nodeId, int maxDepth, CancellationToken cancellationToken) =>
        _impact.AnalyzeAsync(nodeId, maxDepth, cancellationToken);

    public async Task<OverviewReport> GetOverviewAsync(string instance, string database, CancellationToken cancellationToken)
    {
        DatabaseIndex index = await _indexes.GetAsync(instance, database, cancellationToken);
        return _structural.GetOverview(index);
    }

    public Task<AgentResponse> AskAsync(AgentRequest request, CancellationToken cancellationToken) =>
        _agent.AskAsync(request, cancellationToken);
}
