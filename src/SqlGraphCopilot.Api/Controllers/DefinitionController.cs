using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using SqlGraphCopilot.Application.Flow;

namespace SqlGraphCopilot.Api.Controllers;

[ApiController]
[Route("api/objects")]
public sealed class DefinitionController : ControllerBase
{
    private const int MaxChars = 200_000;

    private readonly ProcedureFlowService _flow;

    public DefinitionController(ProcedureFlowService flow)
    {
        _flow = flow;
    }

    [HttpGet("definition")]
    public async Task<IActionResult> Get(
        [FromQuery, BindRequired] string instance,
        [FromQuery, BindRequired] string database,
        [FromQuery, BindRequired] string schema,
        [FromQuery, BindRequired] string name,
        CancellationToken cancellationToken)
    {
        string? definition = await _flow.GetDefinitionAsync(instance, database, schema, name, cancellationToken);
        if (string.IsNullOrWhiteSpace(definition))
        {
            return NotFound(new { message = $"No se pudo leer el código de {schema}.{name}: no existe, está cifrado o falta el permiso VIEW DEFINITION." });
        }

        bool truncated = definition.Length > MaxChars;
        return Ok(new { schema, name, definition = truncated ? definition[..MaxChars] : definition, truncated });
    }
}
