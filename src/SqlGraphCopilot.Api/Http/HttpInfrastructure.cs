using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Usage;
using SqlGraphCopilot.Domain.Exceptions;
using System.Diagnostics;

namespace SqlGraphCopilot.Api.Http;

/// <summary>Traduce excepciones de dominio a ProblemDetails (RFC 7807) sin filtrar detalles internos en errores 500.</summary>
public sealed class DomainExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<DomainExceptionHandler> _logger;

    public DomainExceptionHandler(IProblemDetailsService problemDetails, ILogger<DomainExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        (int status, string title, string detail) = exception switch
        {
            InstanceNotFoundException ex => (StatusCodes.Status404NotFound, "Instancia no encontrada", ex.Message),
            ObjectNotFoundException ex => (StatusCodes.Status404NotFound, "Objeto no encontrado", ex.Message),
            DatabaseNotAccessibleException ex => (StatusCodes.Status422UnprocessableEntity, "Base no accesible", ex.Message),
            DataSourceUnavailableException ex => (StatusCodes.Status503ServiceUnavailable, "Instancia no disponible", ex.Message),
            FormatException ex => (StatusCodes.Status400BadRequest, "Solicitud inválida", ex.Message),
            ArgumentException ex => (StatusCodes.Status400BadRequest, "Solicitud inválida", ex.Message),
            _ => (StatusCodes.Status500InternalServerError, "Error interno", "Ocurrió un error inesperado. Revise los logs del servidor.")
        };

        if (status >= 500)
        {
            _logger.LogError(exception, "Error no controlado en {Path}", httpContext.Request.Path);
        }
        else
        {
            _logger.LogWarning("{Title}: {Detail}", title, detail);
        }

        httpContext.Response.StatusCode = status;
        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail }
        });
    }
}

public static class SecurityHeadersExtensions
{
    /// <summary>Cabeceras defensivas. vis-network se sirve localmente (sin CDN): funciona en redes que bloquean Internet.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            IHeaderDictionary headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            if (!context.Request.Path.StartsWithSegments("/swagger"))
            {
                headers["Content-Security-Policy"] =
                    "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'";
            }
            await next();
        });
}

public sealed class UsageActionFilter : IAsyncActionFilter
{
    private readonly IUsageTracker _tracker;

    public UsageActionFilter(IUsageTracker tracker)
    {
        _tracker = tracker;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        UsageContext usage = new();
        UsageContext.Current = usage;
        Stopwatch watch = Stopwatch.StartNew();
        ActionExecutedContext executed = await next();
        watch.Stop();

        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return;
        }

        try
        {
            object? value = executed.Result is ObjectResult objectResult ? objectResult.Value : null;
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
        catch { }
    }

    private void Record(UsageContext usage, string engine, string reply, string intent, int latencyMs)
    {
        bool deterministic = engine.Equals("deterministic", StringComparison.OrdinalIgnoreCase);
        if (deterministic) engine = "deterministic";
        bool fallback = engine.StartsWith("deterministic-", StringComparison.OrdinalIgnoreCase);
        bool real = usage.ProviderPromptTokens is not null;

        int input = deterministic ? 0 : usage.ProviderPromptTokens ?? _tracker.EstimateTokens(usage.PromptChars);
        int output = deterministic || fallback ? 0 : usage.ProviderCompletionTokens ?? _tracker.EstimateTokens(reply.Length);
        int savedMinify = _tracker.EstimateTokens(usage.MinifiedSavedChars);
        int savedAvoided = deterministic ? _tracker.Options.AvoidedTokensPerDeterministicAnswer : 0;

        if (usage.CacheHit)
        {
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

    private static string? FindMessage(ActionExecutingContext context)
    {
        foreach (object? argument in context.ActionArguments.Values)
        {
            string? message = argument?.GetType().GetProperty("Message")?.GetValue(argument) as string;
            if (message is not null) return message;
        }
        return null;
    }

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
