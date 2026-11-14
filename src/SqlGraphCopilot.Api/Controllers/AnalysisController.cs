using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using SqlGraphCopilot.Api.Contracts;
using SqlGraphCopilot.Application.Analysis;
using SqlGraphCopilot.Application.Indexing;

namespace SqlGraphCopilot.Api.Controllers;

[ApiController]
[Route("api/analysis")]
[Tags("Análisis")]
public sealed class AnalysisController : ControllerBase
{
    private readonly IDatabaseIndexProvider _indexes;
    private readonly ImpactAnalyzer _impactAnalyzer;
    private readonly StructuralAnalyzer _structuralAnalyzer;
    private readonly DependencyClassifier _classifier;

    public AnalysisController(
        IDatabaseIndexProvider indexes,
        ImpactAnalyzer impactAnalyzer,
        StructuralAnalyzer structuralAnalyzer,
        DependencyClassifier classifier)
    {
        _indexes = indexes;
        _impactAnalyzer = impactAnalyzer;
        _structuralAnalyzer = structuralAnalyzer;
        _classifier = classifier;
    }

    [HttpGet("impact", Name = "AnalyzeImpact")]
    [EndpointSummary("¿Qué se rompe si modifico este objeto? (estructural, por datos y en otras bases cargadas)")]
    public async Task<IActionResult> AnalyzeImpact(
        [FromQuery, BindRequired] string nodeId,
        [FromQuery] int? maxDepth,
        CancellationToken ct)
        => Ok(await _impactAnalyzer.AnalyzeAsync(nodeId, maxDepth, ct));

    [HttpGet("overview", Name = "GetOverview")]
    public async Task<IActionResult> GetOverview(
        [FromQuery, BindRequired] string instance,
        [FromQuery, BindRequired] string database,
        CancellationToken ct)
        => Ok(_structuralAnalyzer.GetOverview(await _indexes.GetAsync(instance, database, ct)));

    [HttpGet("spof", Name = "GetSinglePointsOfFailure")]
    public async Task<IActionResult> GetSinglePointsOfFailure(
        [FromQuery, BindRequired] string instance,
        [FromQuery, BindRequired] string database,
        [FromQuery] int? top,
        CancellationToken ct)
        => Ok(_structuralAnalyzer.GetSinglePointsOfFailure(await _indexes.GetAsync(instance, database, ct), Top(top)));

    [HttpGet("coupling", Name = "GetCoupling")]
    public async Task<IActionResult> GetCoupling(
        [FromQuery, BindRequired] string instance,
        [FromQuery, BindRequired] string database,
        [FromQuery] int? top,
        CancellationToken ct)
        => Ok(_structuralAnalyzer.GetCoupling(await _indexes.GetAsync(instance, database, ct), Top(top)));

    [HttpGet("redundancy", Name = "GetRedundancy")]
    public async Task<IActionResult> GetRedundancy(
        [FromQuery, BindRequired] string instance,
        [FromQuery, BindRequired] string database,
        [FromQuery] int? top,
        CancellationToken ct)
        => Ok(_structuralAnalyzer.GetRedundancies(await _indexes.GetAsync(instance, database, ct), Top(top)));

    [HttpGet("bottlenecks", Name = "GetBottlenecks")]
    public async Task<IActionResult> GetBottlenecks(
        [FromQuery, BindRequired] string instance,
        [FromQuery, BindRequired] string database,
        [FromQuery] int? top,
        CancellationToken ct)
        => Ok(_structuralAnalyzer.GetBottlenecks(await _indexes.GetAsync(instance, database, ct), Top(top)));

    [HttpGet("broken-references", Name = "GetBrokenReferences")]
    public async Task<IActionResult> GetBrokenReferences(
        [FromQuery, BindRequired] string instance,
        [FromQuery, BindRequired] string database,
        [FromQuery] int? top,
        CancellationToken ct)
        => Ok(_structuralAnalyzer.GetBrokenReferences(await _indexes.GetAsync(instance, database, ct), Top(top, 50)));

    [HttpPost("deep-scan", Name = "DeepScan")]
    [EndpointSummary("Clasifica lee/escribe/ejecuta en TODOS los módulos de la base (lectura en streaming).")]
    public async Task<IActionResult> DeepScan(
        [FromBody] DatabaseScopeRequest request,
        CancellationToken ct)
        => Ok(await _classifier.DeepScanAsync(await _indexes.GetAsync(request.Instance, request.Database, ct), ct));

    private static int Top(int? top, int fallback = 20) => Math.Clamp(top ?? fallback, 1, 100);
}
