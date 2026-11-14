namespace SqlGraphCopilot.Application.Mishi;

public sealed record RiskFinding(
    string Id,
    string Severity,
    string Title,
    string Detail,
    string Recommendation,
    string? Table,
    bool IsOptimization);

public sealed record ScanResult(
    IReadOnlyList<RiskFinding> Findings,
    IReadOnlyList<string> Tables,
    IReadOnlyList<string> ParseErrors);
