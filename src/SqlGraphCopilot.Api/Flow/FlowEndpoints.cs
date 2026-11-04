using SqlGraphCopilot.Core.Flow;

namespace SqlGraphCopilot.Api.Endpoints;

public static class FlowEndpoints
{
    /// <summary>GET /api/flow/inputs?instance=&amp;database=&amp;schema=&amp;name=  (se llama sobre el grupo "/api").</summary>
    public static RouteGroupBuilder MapFlowEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/flow/inputs", async (
            string instance,
            string database,
            string schema,
            string name,
            ProcedureFlowService service,
            CancellationToken cancellationToken) =>
        {
            ProcedureFlowReport? report = await service.AnalyzeAsync(instance, database, schema, name, cancellationToken);
            return report is null
                ? Results.NotFound(new { message = $"No existe {schema}.{name} en {database}." })
                : Results.Ok(report);
        });
        return api;
    }
}
