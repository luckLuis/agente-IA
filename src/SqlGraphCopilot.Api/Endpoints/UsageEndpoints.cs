using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SqlGraphCopilot.Core.Usage;

namespace SqlGraphCopilot.Api.Endpoints;

public static class UsageEndpoints
{
    /// <summary>GET /api/stats · GET /api/stats/export.csv · POST /api/stats/reset (sobre el grupo "/api").</summary>
    public static RouteGroupBuilder MapUsageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/stats", (UsageTracker tracker) => Results.Ok(tracker.Snapshot()));

        api.MapGet("/stats/export.csv", (UsageTracker tracker) =>
        {
            StringBuilder csv = new();
            csv.AppendLine("fecha_utc,intencion,motor,latencia_ms,tokens_entrada,tokens_salida,estimado,tokens_ahorrados_minify,tokens_ahorrados_sin_llm,cache,minutos_manuales");
            foreach (UsageEntry e in tracker.All())
            {
                csv.AppendLine(string.Join(',', e.At.ToString("o"), e.Intent, e.Engine, e.LatencyMs, e.InputTokens, e.OutputTokens,
                    e.Estimated, e.SavedByMinify, e.SavedByAvoidingLlm, e.CacheHit, e.ManualMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "sqlgraphcopilot-uso.csv");
        });

        api.MapPost("/stats/reset", (UsageTracker tracker) =>
        {
            tracker.Reset();
            return Results.NoContent();
        });
        return api;
    }
}

/// <summary>
/// Mide cada respuesta del copiloto (cualquier endpoint que devuelva un objeto con propiedades Engine y Reply):
/// latencia, motor, tokens (reales si el cliente del LLM los informa; si no, estimados) y ahorro.
/// Se registra con api.AddEndpointFilter&lt;UsageEndpointFilter&gt;().
/// </summary>
public sealed class UsageEndpointFilter : IEndpointFilter
{
    private readonly UsageTracker _tracker;

    public UsageEndpointFilter(UsageTracker tracker)
    {
        _tracker = tracker;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        UsageContext usage = new();
        UsageContext.Current = usage;
        Stopwatch watch = Stopwatch.StartNew();
        object? result = await next(context);
        watch.Stop();

        try
        {
            // Solo las respuestas del copiloto (objeto con Engine y Reply); el resto de endpoints no se toca.
            object? value = result is IValueHttpResult http ? http.Value : result;
            Type? type = value?.GetType();
            System.Reflection.PropertyInfo? engineProperty = type?.GetProperty("Engine");
            System.Reflection.PropertyInfo? replyProperty = type?.GetProperty("Reply");
            if (value is not null && engineProperty is not null && replyProperty is not null)
            {
                string engine = engineProperty.GetValue(value)?.ToString() ?? "desconocido";
                string reply = replyProperty.GetValue(value) as string ?? string.Empty;
                string? realIntent = type!.GetProperty("Intent")?.GetValue(value)?.ToString();
                string intent = realIntent is not null ? MapIntent(realIntent) : GuessIntent(FindMessage(context));
                Record(usage, engine, reply, intent, (int)watch.ElapsedMilliseconds);
            }
        }
        catch { /* las métricas nunca deben romper una respuesta */ }

        return result;
    }

    private void Record(UsageContext usage, string engine, string reply, string intent, int latencyMs)
    {
        bool deterministic = engine.Equals("deterministic", StringComparison.OrdinalIgnoreCase);
        if (deterministic) engine = "deterministic";
        bool fallback = engine.StartsWith("deterministic-", StringComparison.OrdinalIgnoreCase);   // el LLM falló: se envió el prompt pero no hubo respuesta útil
        bool real = usage.ProviderPromptTokens is not null;

        int input = deterministic ? 0 : usage.ProviderPromptTokens ?? _tracker.EstimateTokens(usage.PromptChars);
        int output = deterministic || fallback ? 0 : usage.ProviderCompletionTokens ?? _tracker.EstimateTokens(reply.Length);
        int savedMinify = _tracker.EstimateTokens(usage.MinifiedSavedChars);
        int savedAvoided = deterministic ? _tracker.Options.AvoidedTokensPerDeterministicAnswer : 0;

        if (usage.CacheHit)
        {
            // Respuesta de la caché: no se gastó nada; lo que habría costado cuenta como ahorro.
            savedAvoided = input + output;
            input = 0;
            output = 0;
        }

        _tracker.Record(new UsageEntry(
            At: DateTimeOffset.UtcNow,
            Intent: intent,
            Engine: usage.CacheHit ? "cache" : engine,
            LatencyMs: latencyMs,
            InputTokens: input,
            OutputTokens: output,
            Estimated: !deterministic && !real,
            SavedByMinify: savedMinify,
            SavedByAvoidingLlm: savedAvoided,
            CacheHit: usage.CacheHit,
            ManualMinutes: _tracker.ManualMinutesFor(intent)));
    }

    /// <summary>Busca el texto de la pregunta en los argumentos del endpoint (propiedad Message).</summary>
    private static string? FindMessage(EndpointFilterInvocationContext context)
    {
        foreach (object? argument in context.Arguments)
        {
            string? message = argument?.GetType().GetProperty("Message")?.GetValue(argument) as string;
            if (message is not null) return message;
        }
        return null;
    }

    /// <summary>Intención real del agente (AgentIntent) agrupada en las categorías del panel.</summary>
    private static string MapIntent(string intent) => intent switch
    {
        "MigrateToCode" => "migrate",
        "ExplainLogic" or "Explain" => "explain",
        "Impact" => "impact",
        "SinglePointsOfFailure" or "Coupling" or "Redundancy" or "Bottlenecks" or "BrokenReferences" or "Overview" => "analysis",
        _ => "general"
    };

    private static string GuessIntent(string? message)
    {
        string text = (message ?? string.Empty).ToLowerInvariant();
        if (text.Contains("migr")) return "migrate";
        if (text.Contains("explic") || text.Contains("qué hace") || text.Contains("que hace") || text.Contains("lógica") || text.Contains("logica")) return "explain";
        if (text.Contains("impacto") || text.Contains("rompe") || text.Contains("modifico") || text.Contains("afecta")) return "impact";
        if (text.Contains("duplic") || text.Contains("redundan") || text.Contains("diagnóstico") || text.Contains("diagnostico")
            || text.Contains("acoplamiento") || text.Contains("cuello") || text.Contains("fallo")) return "analysis";
        return "general";
    }
}
