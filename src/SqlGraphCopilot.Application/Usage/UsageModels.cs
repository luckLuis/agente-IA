namespace SqlGraphCopilot.Application.Usage;

public sealed class UsageOptions
{
    /// <summary>USD por millón de tokens de entrada / salida. 0 = no mostrar costo.</summary>
    public double InputPricePerMillion { get; set; }
    public double OutputPricePerMillion { get; set; }

    /// <summary>Tokens que habría gastado un LLM en una respuesta que el motor del grafo resolvió solo (estimación).</summary>
    public int AvoidedTokensPerDeterministicAnswer { get; set; } = 1500;

    /// <summary>Caracteres por token cuando el proveedor no informa el consumo real (estimación).</summary>
    public double CharsPerToken { get; set; } = 3.6;

    /// <summary>Minutos que tomaría hacer la tarea a mano (supuesto editable). Claves: migrate, explain, impact, analysis, general.</summary>
    public Dictionary<string, double> ManualMinutes { get; set; } = new()
    {
        ["migrate"] = 120,
        ["explain"] = 15,
        ["impact"] = 30,
        ["analysis"] = 20,
        ["general"] = 5
    };

    /// <summary>Archivo .jsonl donde se guardan las métricas para conservarlas al reiniciar. Vacío = solo memoria.</summary>
    public string? StoragePath { get; set; }
}

/// <summary>
/// Acumulador por solicitud. El filtro de endpoints lo crea; el PromptBuilder y el cliente del LLM lo completan.
/// Es un objeto mutable compartido, así que lo que se escriba dentro de la llamada lo ve el filtro al terminar.
/// </summary>
public sealed class UsageContext
{
    private static readonly AsyncLocal<UsageContext?> CurrentValue = new();

    public static UsageContext? Current
    {
        get => CurrentValue.Value;
        set => CurrentValue.Value = value;
    }

    private int _promptChars;
    private int _savedChars;

    public int PromptChars => _promptChars;
    public int MinifiedSavedChars => _savedChars;

    /// <summary>Consumo real informado por el proveedor (campo usage de la respuesta). Null si no se informó.</summary>
    public int? ProviderPromptTokens { get; private set; }
    public int? ProviderCompletionTokens { get; private set; }

    /// <summary>true cuando la respuesta salió de la caché y no se llamó al modelo.</summary>
    public bool CacheHit { get; set; }

    public void AddPrompt(int chars, int savedChars)
    {
        Interlocked.Add(ref _promptChars, chars);
        Interlocked.Add(ref _savedChars, savedChars);
    }

    public void ReportProviderUsage(int promptTokens, int completionTokens)
    {
        ProviderPromptTokens = (ProviderPromptTokens ?? 0) + promptTokens;
        ProviderCompletionTokens = (ProviderCompletionTokens ?? 0) + completionTokens;
    }
}

public sealed record UsageEntry(
    DateTimeOffset At,
    string Intent,
    string Engine,
    int LatencyMs,
    int InputTokens,
    int OutputTokens,
    bool Estimated,
    int SavedByMinify,
    int SavedByAvoidingLlm,
    bool CacheHit,
    double ManualMinutes);

public sealed record VerificationEntry(DateTimeOffset At, int Attempt, bool Compiled, int Passed, int Failed, int DurationMs);

public sealed record UsageAssumptions(double InputPricePerMillion, double OutputPricePerMillion, int AvoidedTokensPerDeterministicAnswer, double CharsPerToken, IReadOnlyDictionary<string, double> ManualMinutes);
public sealed record IntentStats(string Intent, int Count, int AvgMs, long Tokens, double SavedMinutes);
public sealed record VerificationStats(int Runs, int GreenFirstTry, int GreenAfterFix, double FirstTryRate, double AvgAttemptsToGreen, int AvgDurationMs);

public sealed record UsageSummary(
    DateTimeOffset Since,
    int Queries,
    int LlmCalls,
    int Deterministic,
    int CacheHits,
    double WithoutLlmPercent,
    long InputTokens,
    long OutputTokens,
    bool TokensEstimated,
    long TokensSavedByMinify,
    long TokensSavedByAvoidingLlm,
    double? CostUsd,
    double? SavedUsd,
    int AvgLatencyMs,
    int AvgLlmLatencyMs,
    int AvgDeterministicLatencyMs,
    int P95LatencyMs,
    double ManualMinutes,
    double AssistantMinutes,
    double SavedMinutes,
    IReadOnlyList<IntentStats> ByIntent,
    VerificationStats Verification,
    IReadOnlyList<UsageEntry> Recent,
    UsageAssumptions Assumptions);
