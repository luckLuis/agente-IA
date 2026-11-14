using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using SqlGraphCopilot.Api.Contracts;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Catalog;
using SqlGraphCopilot.Application.Exploration;
using SqlGraphCopilot.Application.Indexing;
using SqlGraphCopilot.Application.Settings;
using SqlGraphCopilot.Domain;
using SqlGraphCopilot.Domain.Indexing;

namespace SqlGraphCopilot.Api.Controllers;

[ApiController]
[Route("api/instances")]
[Tags("Catálogo")]
public sealed class CatalogController : ControllerBase
{
    private readonly IInstanceRegistry _registry;
    private readonly ICatalogReader _reader;
    private readonly IDatabaseIndexProvider _indexes;

    public CatalogController(
        IInstanceRegistry registry,
        ICatalogReader reader,
        IDatabaseIndexProvider indexes)
    {
        _registry = registry;
        _reader = reader;
        _indexes = indexes;
    }

    [HttpGet("", Name = "ListInstances")]
    [EndpointSummary("Instancias registradas (sin cadenas de conexión).")]
    public IActionResult ListInstances()
        => Ok(_registry.Instances
            .Select(i => new InstanceDto(i.Name, i.Environment, i.Description))
            .ToList());

    [HttpGet("{instance}/databases", Name = "ListDatabases")]
    [EndpointSummary("Bases ONLINE a las que el login tiene acceso.")]
    public async Task<IActionResult> ListDatabases(
        [FromRoute] string instance,
        CancellationToken ct)
    {
        SqlInstanceOptions registered = _registry.GetRequired(instance);
        IReadOnlyList<DatabaseInfo> databases = await _reader.ListDatabasesAsync(registered.Name, ct);
        return Ok(databases
            .Where(d => _registry.IsDatabaseAllowed(registered.Name, d.Name))
            .Select(d => new DatabaseDto(d.Name, d.CompatibilityLevel))
            .ToList());
    }

    [HttpGet("{instance}/databases/{database}/summary", Name = "GetDatabaseSummary")]
    [EndpointSummary("Construye (o reutiliza) el índice de la base y devuelve sus métricas.")]
    public async Task<IActionResult> GetDatabaseSummary(
        [FromRoute] string instance,
        [FromRoute] string database,
        CancellationToken ct)
    {
        DatabaseIndex index = await _indexes.GetAsync(instance, database, ct);
        return Ok(index.GetSummary());
    }

    [HttpGet("{instance}/databases/{database}/objects", Name = "SearchObjects")]
    [EndpointSummary("Búsqueda paginada en el catálogo de la base (no carga el grafo en el cliente).")]
    public async Task<IActionResult> SearchObjects(
        [FromRoute] string instance,
        [FromRoute] string database,
        [FromQuery] string? search,
        [FromQuery] string? kinds,
        [FromQuery] bool? includeExternal,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken ct)
    {
        DatabaseIndex index = await _indexes.GetAsync(instance, database, ct);
        ObjectSearchQuery query = new(search, ParseKinds(kinds), includeExternal ?? false, page ?? 1, pageSize ?? 50);
        PagedResult<GraphNode> result = index.Search(query);

        List<NodeDto> items = result.Items
            .Select(n => ApiMapper.ToDto(n, new NodeDegree(index.Graph.FanIn(n.Id), index.Graph.FanOut(n.Id)), true, null, index.Instance, index.Database))
            .ToList();
        return Ok(new PagedResult<NodeDto>(items, result.Page, result.PageSize, result.TotalCount));
    }

    [HttpPost("{instance}/databases/{database}/refresh", Name = "RefreshDatabase")]
    [EndpointSummary("Descarta el índice en memoria; la próxima consulta relee el catálogo.")]
    public IActionResult RefreshDatabase(
        [FromRoute] string instance,
        [FromRoute] string database)
    {
        _indexes.Invalidate(_registry.GetRequired(instance).Name, database);
        return NoContent();
    }

    private static IReadOnlyCollection<NodeKind>? ParseKinds(string? kinds)
    {
        if (string.IsNullOrWhiteSpace(kinds))
        {
            return null;
        }

        List<NodeKind> parsed = new();
        foreach (string part in kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse(part, ignoreCase: true, out NodeKind kind))
            {
                throw new ArgumentException($"Tipo de objeto desconocido: '{part}'.");
            }
            parsed.Add(kind);
        }
        return parsed;
    }
}
