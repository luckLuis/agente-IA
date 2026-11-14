using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Flow;
using SqlGraphCopilot.Application.Mishi;
using SqlGraphCopilot.Application.Usage;

namespace SqlGraphCopilot.Api.Controllers;

public sealed record MishiRequest(string? Instance, string? Database, string? Schema, string? Name, string? Sql);

[ApiController]
[Route("api/mishi")]
public sealed class MishiController : ControllerBase
{
    private const int MaxSqlChars = 200_000;

    private readonly MishiService _mishi;
    private readonly ProcedureFlowService _flow;
    private readonly IUsageTracker _usage;

    public MishiController(MishiService mishi, ProcedureFlowService flow, IUsageTracker usage)
    {
        _mishi = mishi;
        _flow = flow;
        _usage = usage;
    }

    [HttpGet("state")]
    public IActionResult GetState() => Ok(_mishi.Progress());

    [HttpPost("analyze")]
    public async Task<IActionResult> Analyze([FromBody] MishiRequest request, CancellationToken cancellationToken)
    {
        Stopwatch watch = Stopwatch.StartNew();
        string? sql = request.Sql;
        string? label = null;

        if (string.IsNullOrWhiteSpace(sql))
        {
            if (request.Instance is null || request.Database is null || request.Schema is null || request.Name is null)
            {
                return BadRequest(new { message = "Envíe el SQL a analizar, o instancia, base, esquema y nombre del objeto." });
            }
            sql = await _flow.GetDefinitionAsync(request.Instance, request.Database, request.Schema, request.Name, cancellationToken);
            if (sql is null)
            {
                return NotFound(new { message = $"No puedo leer el código de {request.Schema}.{request.Name} (no existe, está cifrado o falta VIEW DEFINITION)." });
            }
            label = $"{request.Schema}.{request.Name}";
        }
        if (sql.Length > MaxSqlChars)
        {
            return BadRequest(new { message = "El SQL es demasiado grande." });
        }

        MishiReply reply = _mishi.Analyze(sql, label);

        _usage.Record(new UsageEntry(DateTimeOffset.UtcNow, "analysis", "deterministic", (int)watch.ElapsedMilliseconds, 0, 0, false,
            0, _usage.Options.AvoidedTokensPerDeterministicAnswer, false, _usage.ManualMinutesFor("analysis")));
        return Ok(reply);
    }
}
