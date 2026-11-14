using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using SqlGraphCopilot.Api.Contracts;
using SqlGraphCopilot.Application.Exploration;
using SqlGraphCopilot.Domain;

namespace SqlGraphCopilot.Api.Controllers;

[ApiController]
[Route("api/graph")]
[Tags("Grafo")]
public sealed class GraphController : ControllerBase
{
    private readonly GraphExplorer _explorer;

    public GraphController(GraphExplorer explorer)
    {
        _explorer = explorer;
    }

    [HttpGet("hubs", Name = "GetHubs")]
    [EndpointSummary("Vista inicial: los objetos más referenciados y sus relaciones.")]
    public async Task<IActionResult> GetHubs(
        [FromQuery, BindRequired] string instance,
        [FromQuery, BindRequired] string database,
        [FromQuery] int? top,
        CancellationToken ct)
    {
        ExplorationResult result = await _explorer.GetHubsAsync(instance, database, top, ct);
        return Ok(ApiMapper.ToDto(result));
    }

    [HttpGet("neighborhood", Name = "GetNeighborhood")]
    [EndpointSummary("Vecindario a N saltos. Para un nodo externo carga solo la rama de su base.")]
    public async Task<IActionResult> GetNeighborhood(
        [FromQuery, BindRequired] string nodeId,
        [FromQuery] int? depth,
        [FromQuery] TraversalDirection? direction,
        [FromQuery] int? maxNodes,
        CancellationToken ct)
    {
        ExplorationResult result = await _explorer.GetNeighborhoodAsync(nodeId, depth, direction ?? TraversalDirection.Both, maxNodes, ct);
        return result.Status switch
        {
            ExplorationStatus.Ok => Ok(ApiMapper.ToDto(result)),
            ExplorationStatus.NotFound => Problem(result.Message, statusCode: StatusCodes.Status404NotFound, title: "Objeto no encontrado"),
            _ => Problem(result.Message, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Rama no disponible")
        };
    }
}
