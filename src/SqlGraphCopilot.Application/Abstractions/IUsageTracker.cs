using SqlGraphCopilot.Application.Usage;

namespace SqlGraphCopilot.Application.Abstractions;

/// <summary>Métricas de uso del agente: tokens, tiempo, costo, ahorro y calidad.</summary>
public interface IUsageTracker
{
    UsageOptions Options { get; }
    double ManualMinutesFor(string intent);
    int EstimateTokens(int chars);
    void Record(UsageEntry entry);
    void RecordVerification(VerificationEntry entry);
    void Reset();
    IReadOnlyList<UsageEntry> All();
    UsageSummary Snapshot();
}
