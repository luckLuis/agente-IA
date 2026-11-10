using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SqlGraphCopilot.Core.Flow;
using SqlGraphCopilot.Core.Mishi;
using SqlGraphCopilot.Core.Usage;

namespace SqlGraphCopilot.Api.Endpoints;

/// <summary>Sql: texto a analizar. Si no viene, se lee el SP indicado (Instance, Database, Schema, Name) desde el catálogo.</summary>
public sealed record MishiRequest(string? Instance, string? Database, string? Schema, string? Name, string? Sql);

public static class MishiEndpoints
{
    private const int MaxSqlChars = 200_000;

    /// <summary>POST /api/mishi/analyze · GET /api/mishi/state  (sobre el grupo "/api").</summary>
    public static RouteGroupBuilder MapMishiEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/mishi/state", (MishiService mishi) => Results.Ok(mishi.Progress()));

        api.MapPost("/mishi/analyze", async (
            MishiRequest request,
            MishiService mishi,
            ProcedureFlowService flow,
            UsageTracker usage,
            CancellationToken cancellationToken) =>
        {
            Stopwatch watch = Stopwatch.StartNew();
            string? sql = request.Sql;
            string? label = null;

            if (string.IsNullOrWhiteSpace(sql))
            {
                if (request.Instance is null || request.Database is null || request.Schema is null || request.Name is null)
                {
                    return Results.BadRequest(new { message = "Envíe el SQL a analizar, o instancia, base, esquema y nombre del objeto." });
                }
                sql = await flow.GetDefinitionAsync(request.Instance, request.Database, request.Schema, request.Name, cancellationToken);
                if (sql is null) return Results.NotFound(new { message = $"No puedo leer el código de {request.Schema}.{request.Name} (no existe, está cifrado o falta VIEW DEFINITION)." });
                label = $"{request.Schema}.{request.Name}";
            }
            if (sql.Length > MaxSqlChars) return Results.BadRequest(new { message = "El SQL es demasiado grande." });

            MishiReply reply = mishi.Analyze(sql, label);

            // Cuenta como una respuesta resuelta sin LLM en las estadísticas.
            usage.Record(new UsageEntry(DateTimeOffset.UtcNow, "analysis", "deterministic", (int)watch.ElapsedMilliseconds, 0, 0, false,
                0, usage.Options.AvoidedTokensPerDeterministicAnswer, false, usage.ManualMinutesFor("analysis")));
            return Results.Ok(reply);
        });
        return api;
    }
}
