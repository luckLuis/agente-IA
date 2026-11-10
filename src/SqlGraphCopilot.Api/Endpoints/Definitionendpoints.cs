using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using SqlGraphCopilot.Core.Flow;

namespace SqlGraphCopilot.Api.Endpoints;

/// <summary>Código T-SQL de un procedimiento, función, trigger o vista, para mostrarlo en el panel de detalle.</summary>
public static class DefinitionEndpoints
{
    private const int MaxDefinitionChars = 500_000;

    /// <summary>GET /api/objects/definition?instance=&amp;database=&amp;schema=&amp;name=  (sobre el grupo "/api").</summary>
    public static RouteGroupBuilder MapDefinitionEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/objects/definition", async (
            [FromQuery] string? instance,
            [FromQuery] string? database,
            [FromQuery] string? schema,
            [FromQuery] string? name,
            ProcedureFlowService flow,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(instance) || string.IsNullOrWhiteSpace(database)
                || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest(new { message = "Indique instancia, base, esquema y nombre del objeto." });
            }

            // Mismo método que usa Mishi: solo lee metadata (sys.sql_modules), nunca datos.
            string? sql = await flow.GetDefinitionAsync(instance, database, schema, name, cancellationToken);
            if (sql is null)
            {
                return Results.NotFound(new { message = $"No puedo leer el código de {schema}.{name} (no existe, está cifrado o falta VIEW DEFINITION)." });
            }

            bool truncated = sql.Length > MaxDefinitionChars;
            return Results.Ok(new
            {
                schema,
                name,
                definition = truncated ? sql[..MaxDefinitionChars] : sql,
                lines = sql.Count(c => c == '\n') + 1,
                truncated
            });
        })
            .WithTags("Objetos")
            .WithName("GetObjectDefinition")
            .WithSummary("Código T-SQL del objeto (solo lectura del catálogo).");

        return api;
    }
}
