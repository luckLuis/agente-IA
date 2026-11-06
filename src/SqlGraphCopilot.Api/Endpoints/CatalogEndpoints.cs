using Microsoft.AspNetCore.Mvc;
using SqlGraphCopilot.Api.Contracts;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Catalog;
using SqlGraphCopilot.Application.Exploration;
using SqlGraphCopilot.Application.Indexing;
using SqlGraphCopilot.Application.Settings;
using SqlGraphCopilot.Domain;
using SqlGraphCopilot.Domain.Indexing;

namespace SqlGraphCopilot.Api.Endpoints;

/// <summary>Paso 1 del flujo: seleccionar instancia y base antes de cualquier consulta.</summary>
public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder api)
    {
        RouteGroupBuilder group = api.MapGroup("/instances").WithTags("Catálogo");

        group.MapGet("/", (IInstanceRegistry registry) =>
            registry.Instances
                .Select(i => new InstanceDto(i.Name, i.Environment, i.Description))
                .ToList())
            .WithName("ListInstances")
            .WithSummary("Instancias registradas (sin cadenas de conexión).");

        group.MapGet("/{instance}/databases", async (string instance, IInstanceRegistry registry, ICatalogReader reader, CancellationToken ct) =>
        {
            SqlInstanceOptions registered = registry.GetRequired(instance);
            IReadOnlyList<DatabaseInfo> databases = await reader.ListDatabasesAsync(registered.Name, ct);
            return databases
                .Where(d => registry.IsDatabaseAllowed(registered.Name, d.Name))
                .Select(d => new DatabaseDto(d.Name, d.CompatibilityLevel))
                .ToList();
        })
            .WithName("ListDatabases")
            .WithSummary("Bases ONLINE a las que el login tiene acceso.");

        group.MapGet("/{instance}/databases/{database}/summary", async (string instance, string database, IDatabaseIndexProvider indexes, CancellationToken ct) =>
        {
            DatabaseIndex index = await indexes.GetAsync(instance, database, ct);
            return index.GetSummary();
        })
            .WithName("GetDatabaseSummary")
            .WithSummary("Construye (o reutiliza) el índice de la base y devuelve sus métricas.");

        group.MapGet("/{instance}/databases/{database}/objects", async (
            string instance,
            string database,
            [FromQuery] string? search,
            [FromQuery] string? kinds,
            [FromQuery] bool? includeExternal,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            IDatabaseIndexProvider indexes,
            CancellationToken ct) =>
        {
            DatabaseIndex index = await indexes.GetAsync(instance, database, ct);
            ObjectSearchQuery query = new(search, ParseKinds(kinds), includeExternal ?? false, page ?? 1, pageSize ?? 50);
            PagedResult<GraphNode> result = index.Search(query);

            List<NodeDto> items = result.Items
                .Select(n => ApiMapper.ToDto(n, new NodeDegree(index.Graph.FanIn(n.Id), index.Graph.FanOut(n.Id)), true, null, index.Instance, index.Database))
                .ToList();
            return new PagedResult<NodeDto>(items, result.Page, result.PageSize, result.TotalCount);
        })
            .WithName("SearchObjects")
            .WithSummary("Búsqueda paginada en el catálogo de la base (no carga el grafo en el cliente).");

        group.MapPost("/{instance}/databases/{database}/refresh", (string instance, string database, IInstanceRegistry registry, IDatabaseIndexProvider indexes) =>
        {
            indexes.Invalidate(registry.GetRequired(instance).Name, database);
            return Results.NoContent();
        })
            .WithName("RefreshDatabase")
            .WithSummary("Descarta el índice en memoria; la próxima consulta relee el catálogo.");

        return api;
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
