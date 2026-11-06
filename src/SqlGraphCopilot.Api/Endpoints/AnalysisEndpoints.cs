using Microsoft.AspNetCore.Mvc;
using SqlGraphCopilot.Api.Contracts;
using SqlGraphCopilot.Application.Analysis;
using SqlGraphCopilot.Application.Indexing;

namespace SqlGraphCopilot.Api.Endpoints;

public static class AnalysisEndpoints
{
    public static RouteGroupBuilder MapAnalysisEndpoints(this RouteGroupBuilder api)
    {
        RouteGroupBuilder group = api.MapGroup("/analysis").WithTags("Análisis");

        group.MapGet("/impact", async ([FromQuery] string nodeId, [FromQuery] int? maxDepth, ImpactAnalyzer analyzer, CancellationToken ct) =>
            await analyzer.AnalyzeAsync(nodeId, maxDepth, ct))
            .WithName("AnalyzeImpact")
            .WithSummary("¿Qué se rompe si modifico este objeto? (estructural, por datos y en otras bases cargadas)");

        group.MapGet("/overview", async ([FromQuery] string instance, [FromQuery] string database, IDatabaseIndexProvider indexes, StructuralAnalyzer analyzer, CancellationToken ct) =>
            analyzer.GetOverview(await indexes.GetAsync(instance, database, ct)))
            .WithName("GetOverview");

        group.MapGet("/spof", async ([FromQuery] string instance, [FromQuery] string database, [FromQuery] int? top, IDatabaseIndexProvider indexes, StructuralAnalyzer analyzer, CancellationToken ct) =>
            analyzer.GetSinglePointsOfFailure(await indexes.GetAsync(instance, database, ct), Top(top)))
            .WithName("GetSinglePointsOfFailure");

        group.MapGet("/coupling", async ([FromQuery] string instance, [FromQuery] string database, [FromQuery] int? top, IDatabaseIndexProvider indexes, StructuralAnalyzer analyzer, CancellationToken ct) =>
            analyzer.GetCoupling(await indexes.GetAsync(instance, database, ct), Top(top)))
            .WithName("GetCoupling");

        group.MapGet("/redundancy", async ([FromQuery] string instance, [FromQuery] string database, [FromQuery] int? top, IDatabaseIndexProvider indexes, StructuralAnalyzer analyzer, CancellationToken ct) =>
            analyzer.GetRedundancies(await indexes.GetAsync(instance, database, ct), Top(top)))
            .WithName("GetRedundancy");

        group.MapGet("/bottlenecks", async ([FromQuery] string instance, [FromQuery] string database, [FromQuery] int? top, IDatabaseIndexProvider indexes, StructuralAnalyzer analyzer, CancellationToken ct) =>
            analyzer.GetBottlenecks(await indexes.GetAsync(instance, database, ct), Top(top)))
            .WithName("GetBottlenecks");

        group.MapGet("/broken-references", async ([FromQuery] string instance, [FromQuery] string database, [FromQuery] int? top, IDatabaseIndexProvider indexes, StructuralAnalyzer analyzer, CancellationToken ct) =>
            analyzer.GetBrokenReferences(await indexes.GetAsync(instance, database, ct), Top(top, 50)))
            .WithName("GetBrokenReferences");

        group.MapPost("/deep-scan", async ([FromBody] DatabaseScopeRequest request, IDatabaseIndexProvider indexes, DependencyClassifier classifier, CancellationToken ct) =>
            await classifier.DeepScanAsync(await indexes.GetAsync(request.Instance, request.Database, ct), ct))
            .WithName("DeepScan")
            .WithSummary("Clasifica lee/escribe/ejecuta en TODOS los módulos de la base (lectura en streaming).");

        return api;
    }

    private static int Top(int? top, int fallback = 20) => Math.Clamp(top ?? fallback, 1, 100);
}
