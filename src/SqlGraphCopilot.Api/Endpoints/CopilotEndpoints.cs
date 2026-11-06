using Microsoft.AspNetCore.Mvc;
using SqlGraphCopilot.Api.Contracts;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Agent;

namespace SqlGraphCopilot.Api.Endpoints;

public static class CopilotEndpoints
{
    private const int MaxMessageLength = 2000;
    private const int MaxScriptLength = 2_000_000;

    public static RouteGroupBuilder MapCopilotEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/copilot/chat", async ([FromBody] ChatRequestDto request, SqlCopilotAgent agent, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.Problem("El mensaje no puede estar vacío.", statusCode: StatusCodes.Status400BadRequest);
            }
            if (request.Message.Length > MaxMessageLength)
            {
                return Results.Problem($"El mensaje supera {MaxMessageLength} caracteres.", statusCode: StatusCodes.Status400BadRequest);
            }
            if (string.IsNullOrWhiteSpace(request.Instance) || string.IsNullOrWhiteSpace(request.Database))
            {
                return Results.Problem("Seleccione una instancia y una base antes de preguntar.", statusCode: StatusCodes.Status400BadRequest);
            }

            AgentResponse response = await agent.AskAsync(
                new AgentRequest(request.Instance, request.Database, request.Message.Trim(), request.FocusNodeId),
                ct);
            return Results.Ok(response);
        })
            .WithTags("Copiloto")
            .WithName("CopilotChat")
            .WithSummary("Pregunta en lenguaje natural sobre la base seleccionada.");

        api.MapPost("/sql/parse", ([FromBody] SqlScriptRequest request, ITSqlParsingService parser) =>
        {
            if (request.Script is null || request.Script.Length > MaxScriptLength)
            {
                return Results.Problem("Script vacío o demasiado grande.", statusCode: StatusCodes.Status400BadRequest);
            }
            return Results.Ok(parser.ParseSqlScript(request.Script));
        })
            .WithTags("Parser")
            .WithName("ParseSqlScript")
            .WithSummary("Analiza un script T-SQL: lecturas, escrituras, EXEC, SQL dinámico, linked servers.");

        return api;
    }
}
