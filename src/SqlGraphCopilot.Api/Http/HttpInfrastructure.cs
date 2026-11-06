using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SqlGraphCopilot.Domain.Exceptions;

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
