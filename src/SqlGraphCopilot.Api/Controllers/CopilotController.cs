using Microsoft.AspNetCore.Mvc;
using SqlGraphCopilot.Api.Contracts;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Agent;

namespace SqlGraphCopilot.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class CopilotController : ControllerBase
{
    private const int MaxMessageLength = 2000;
    private const int MaxScriptLength = 2_000_000;

    private readonly SqlCopilotAgent _agent;
    private readonly ITSqlParsingService _parser;

    public CopilotController(SqlCopilotAgent agent, ITSqlParsingService parser)
    {
        _agent = agent;
        _parser = parser;
    }

    [HttpPost("copilot/chat", Name = "CopilotChat")]
    [Tags("Copiloto")]
    [EndpointSummary("Pregunta en lenguaje natural sobre la base seleccionada.")]
    public async Task<IActionResult> Chat([FromBody] ChatRequestDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Problem("El mensaje no puede estar vacío.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (request.Message.Length > MaxMessageLength)
        {
            return Problem($"El mensaje supera {MaxMessageLength} caracteres.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (string.IsNullOrWhiteSpace(request.Instance) || string.IsNullOrWhiteSpace(request.Database))
        {
            return Problem("Seleccione una instancia y una base antes de preguntar.", statusCode: StatusCodes.Status400BadRequest);
        }

        AgentResponse response = await _agent.AskAsync(
            new AgentRequest(request.Instance, request.Database, request.Message.Trim(), request.FocusNodeId),
            ct);
        return Ok(response);
    }

    [HttpPost("sql/parse", Name = "ParseSqlScript")]
    [Tags("Parser")]
    [EndpointSummary("Analiza un script T-SQL: lecturas, escrituras, EXEC, SQL dinámico, linked servers.")]
    public IActionResult ParseSqlScript([FromBody] SqlScriptRequest request)
    {
        if (request.Script is null || request.Script.Length > MaxScriptLength)
        {
            return Problem("Script vacío o demasiado grande.", statusCode: StatusCodes.Status400BadRequest);
        }
        return Ok(_parser.ParseSqlScript(request.Script));
    }
}
