using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Usage;

namespace SqlGraphCopilot.Api.Controllers;

[ApiController]
[Route("api/stats")]
public sealed class UsageController : ControllerBase
{
    private readonly IUsageTracker _tracker;

    public UsageController(IUsageTracker tracker)
    {
        _tracker = tracker;
    }

    [HttpGet("")]
    public IActionResult GetStats() => Ok(_tracker.Snapshot());

    [HttpGet("export.csv")]
    public IActionResult ExportCsv()
    {
        StringBuilder csv = new();
        csv.AppendLine("fecha_utc,intencion,motor,latencia_ms,tokens_entrada,tokens_salida,estimado,tokens_ahorrados_minify,tokens_ahorrados_sin_llm,cache,minutos_manuales");
        foreach (UsageEntry e in _tracker.All())
        {
            csv.AppendLine(string.Join(',', e.At.ToString("o"), e.Intent, e.Engine, e.LatencyMs, e.InputTokens, e.OutputTokens,
                e.Estimated, e.SavedByMinify, e.SavedByAvoidingLlm, e.CacheHit, e.ManualMinutes.ToString(CultureInfo.InvariantCulture)));
        }
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "sqlgraphcopilot-uso.csv");
    }

    [HttpPost("reset")]
    public IActionResult Reset()
    {
        _tracker.Reset();
        return NoContent();
    }
}
