using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SqlGraphCopilot.Core.Usage;

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

/// <summary>Métricas de uso del agente: tokens, tiempo, costo, ahorro y calidad. Singleton, seguro entre hilos.</summary>
public sealed class UsageTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly List<UsageEntry> _entries = new();
    private readonly List<VerificationEntry> _verifications = new();
    private readonly UsageOptions _options;
    private DateTimeOffset _since = DateTimeOffset.UtcNow;

    public UsageTracker(IOptions<UsageOptions> options)
    {
        _options = options.Value;
        Load();
    }

    public UsageOptions Options => _options;

    public double ManualMinutesFor(string intent) =>
        _options.ManualMinutes.TryGetValue(intent, out double minutes) ? minutes : _options.ManualMinutes.GetValueOrDefault("general", 5);

    public int EstimateTokens(int chars) => chars <= 0 ? 0 : (int)Math.Ceiling(chars / Math.Max(1.0, _options.CharsPerToken));

    public void Record(UsageEntry entry)
    {
        lock (_gate) _entries.Add(entry);
        Persist("usage", entry);
    }

    public void RecordVerification(VerificationEntry entry)
    {
        lock (_gate) _verifications.Add(entry);
        Persist("verification", entry);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _entries.Clear();
            _verifications.Clear();
            _since = DateTimeOffset.UtcNow;
        }
        if (!string.IsNullOrWhiteSpace(_options.StoragePath))
        {
            try { File.Delete(_options.StoragePath); } catch { /* sin archivo */ }
        }
    }

    public IReadOnlyList<UsageEntry> All()
    {
        lock (_gate) return [.. _entries];
    }

    public UsageSummary Snapshot()
    {
        List<UsageEntry> entries;
        List<VerificationEntry> verifications;
        DateTimeOffset since;
        lock (_gate)
        {
            entries = [.. _entries];
            verifications = [.. _verifications];
            since = _since;
        }

        int queries = entries.Count;
        int deterministic = entries.Count(e => e.Engine == "deterministic");
        int cacheHits = entries.Count(e => e.CacheHit);
        int llm = queries - deterministic - cacheHits;

        long input = entries.Sum(e => (long)e.InputTokens);
        long output = entries.Sum(e => (long)e.OutputTokens);
        long savedMinify = entries.Sum(e => (long)e.SavedByMinify);
        long savedAvoided = entries.Sum(e => (long)e.SavedByAvoidingLlm);

        bool priced = _options.InputPricePerMillion > 0 || _options.OutputPricePerMillion > 0;
        double? cost = priced ? input / 1e6 * _options.InputPricePerMillion + output / 1e6 * _options.OutputPricePerMillion : null;
        double? savedUsd = priced ? (savedMinify + savedAvoided) / 1e6 * _options.InputPricePerMillion : null;

        double manual = entries.Sum(e => e.ManualMinutes);
        double assistant = entries.Sum(e => e.LatencyMs) / 60000.0;

        List<int> sorted = entries.Select(e => e.LatencyMs).OrderBy(x => x).ToList();
        int Avg(IEnumerable<UsageEntry> items) => items.Any() ? (int)items.Average(e => e.LatencyMs) : 0;

        List<IntentStats> byIntent = entries
            .GroupBy(e => e.Intent)
            .Select(g => new IntentStats(
                g.Key,
                g.Count(),
                (int)g.Average(e => e.LatencyMs),
                g.Sum(e => (long)e.InputTokens + e.OutputTokens),
                Math.Round(Math.Max(0, g.Sum(e => e.ManualMinutes) - g.Sum(e => e.LatencyMs) / 60000.0), 1)))
            .OrderByDescending(s => s.Count)
            .ToList();

        // Calidad: ¿el código migrado pasó las pruebas al primer intento o tras corregirse?
        int firstRuns = verifications.Count(v => v.Attempt == 0);
        int firstTry = verifications.Count(v => v.Attempt == 0 && v.Compiled && v.Failed == 0 && v.Passed > 0);
        List<VerificationEntry> greens = verifications.Where(v => v.Compiled && v.Failed == 0 && v.Passed > 0).ToList();
        int afterFix = greens.Count(v => v.Attempt > 0);
        VerificationStats verification = new(
            Runs: verifications.Count,
            GreenFirstTry: firstTry,
            GreenAfterFix: afterFix,
            FirstTryRate: firstRuns > 0 ? Math.Round(100.0 * firstTry / firstRuns, 1) : 0,
            AvgAttemptsToGreen: greens.Count > 0 ? Math.Round(greens.Average(v => v.Attempt + 1), 2) : 0,
            AvgDurationMs: verifications.Count > 0 ? (int)verifications.Average(v => v.DurationMs) : 0);

        return new UsageSummary(
            Since: since,
            Queries: queries,
            LlmCalls: llm,
            Deterministic: deterministic,
            CacheHits: cacheHits,
            WithoutLlmPercent: queries > 0 ? Math.Round(100.0 * (deterministic + cacheHits) / queries, 1) : 0,
            InputTokens: input,
            OutputTokens: output,
            TokensEstimated: entries.Any(e => e.Estimated),
            TokensSavedByMinify: savedMinify,
            TokensSavedByAvoidingLlm: savedAvoided,
            CostUsd: cost,
            SavedUsd: savedUsd,
            AvgLatencyMs: Avg(entries),
            AvgLlmLatencyMs: Avg(entries.Where(e => e.Engine != "deterministic" && !e.CacheHit)),
            AvgDeterministicLatencyMs: Avg(entries.Where(e => e.Engine == "deterministic")),
            P95LatencyMs: sorted.Count > 0 ? sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.95) - 1)] : 0,
            ManualMinutes: Math.Round(manual, 1),
            AssistantMinutes: Math.Round(assistant, 1),
            SavedMinutes: Math.Round(Math.Max(0, manual - assistant), 1),
            ByIntent: byIntent,
            Verification: verification,
            Recent: entries.TakeLast(15).Reverse().ToList(),
            Assumptions: new UsageAssumptions(_options.InputPricePerMillion, _options.OutputPricePerMillion, _options.AvoidedTokensPerDeterministicAnswer, _options.CharsPerToken, new Dictionary<string, double>(_options.ManualMinutes)));
    }

    // ---- Persistencia opcional (una línea JSON por evento) --------------------------------------

    private sealed record Stored(string Type, JsonElement Data);

    private void Persist<T>(string type, T entry)
    {
        if (string.IsNullOrWhiteSpace(_options.StoragePath)) return;
        try
        {
            string line = JsonSerializer.Serialize(new Stored(type, JsonSerializer.SerializeToElement(entry, JsonOptions)), JsonOptions);
            lock (_gate) File.AppendAllText(_options.StoragePath, line + Environment.NewLine);
        }
        catch { /* las métricas nunca deben romper una respuesta */ }
    }

    private void Load()
    {
        if (string.IsNullOrWhiteSpace(_options.StoragePath) || !File.Exists(_options.StoragePath)) return;
        try
        {
            foreach (string line in File.ReadLines(_options.StoragePath))
            {
                Stored? stored = JsonSerializer.Deserialize<Stored>(line, JsonOptions);
                if (stored is null) continue;
                if (stored.Type == "usage" && stored.Data.Deserialize<UsageEntry>(JsonOptions) is { } usage) _entries.Add(usage);
                else if (stored.Type == "verification" && stored.Data.Deserialize<VerificationEntry>(JsonOptions) is { } check) _verifications.Add(check);
            }
            if (_entries.Count > 0) _since = _entries.Min(e => e.At);
        }
        catch { /* archivo dañado: se empieza de cero */ }
    }
}
