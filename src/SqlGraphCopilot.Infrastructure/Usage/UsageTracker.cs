using System.Text.Json;
using Microsoft.Extensions.Options;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Usage;

namespace SqlGraphCopilot.Infrastructure.Usage;

/// <summary>Métricas de uso del agente: tokens, tiempo, costo, ahorro y calidad. Singleton, seguro entre hilos.</summary>
public sealed class UsageTracker : IUsageTracker
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
