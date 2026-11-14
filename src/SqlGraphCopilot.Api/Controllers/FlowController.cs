using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using SqlGraphCopilot.Application.Flow;

namespace SqlGraphCopilot.Api.Controllers;

[ApiController]
[Route("api/flow")]
public sealed class FlowController : ControllerBase
{
    private readonly ProcedureFlowService _service;

    public FlowController(ProcedureFlowService service)
    {
        _service = service;
    }

    [HttpGet("inputs")]
    public async Task<IActionResult> GetInputs(
        [FromQuery, BindRequired] string instance,
        [FromQuery, BindRequired] string database,
        [FromQuery, BindRequired] string schema,
        [FromQuery, BindRequired] string name,
        CancellationToken cancellationToken)
    {
        ProcedureFlowReport? report = await _service.AnalyzeAsync(instance, database, schema, name, cancellationToken);
        return report is null
            ? NotFound(new { message = $"No existe {schema}.{name} en {database}." })
            : Ok(report);
    }
}
