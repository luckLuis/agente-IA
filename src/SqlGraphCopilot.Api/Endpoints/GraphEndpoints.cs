using Microsoft.AspNetCore.Mvc;
using SqlGraphCopilot.Api.Contracts;
using SqlGraphCopilot.Application.Exploration;
using SqlGraphCopilot.Domain;

namespace SqlGraphCopilot.Api.Endpoints;

/// <summary>Carga on-demand del grafo: hubs iniciales y vecindario por nodo (incluye expandir ramas externas).</summary>
public static class GraphEndpoints
{
    public static RouteGroupBuilder MapGraphEndpoints(this RouteGroupBuilder api)
    {
        RouteGroupBuilder group = api.MapGroup("/graph").WithTags("Grafo");

        group.MapGet("/hubs", async (
            [FromQuery] string instance,
            [FromQuery] string database,
            [FromQuery] int? top,
            GraphExplorer explorer,
            CancellationToken ct) =>
        {
            ExplorationResult result = await explorer.GetHubsAsync(instance, database, top, ct);
            return Results.Ok(ApiMapper.ToDto(result));
        })
            .WithName("GetHubs")
            .WithSummary("Vista inicial: los objetos más referenciados y sus relaciones.");

        group.MapGet("/neighborhood", async (
            [FromQuery] string nodeId,
            [FromQuery] int? depth,
            [FromQuery] TraversalDirection? direction,
            [FromQuery] int? maxNodes,
            GraphExplorer explorer,
            CancellationToken ct) =>
        {
            ExplorationResult result = await explorer.GetNeighborhoodAsync(nodeId, depth, direction ?? TraversalDirection.Both, maxNodes, ct);
            return result.Status switch
            {
                ExplorationStatus.Ok => Results.Ok(ApiMapper.ToDto(result)),
                ExplorationStatus.NotFound => Results.Problem(result.Message, statusCode: StatusCodes.Status404NotFound, title: "Objeto no encontrado"),
                _ => Results.Problem(result.Message, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Rama no disponible")
            };
        })
            .WithName("GetNeighborhood")
            .WithSummary("Vecindario a N saltos. Para un nodo externo carga solo la rama de su base.");

        return api;
    }
}
