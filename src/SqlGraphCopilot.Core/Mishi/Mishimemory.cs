using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SqlGraphCopilot.Core.Mishi;

public sealed class MishiOptions
{
    /// <summary>Archivo JSON donde Mishi guarda lo que aprende. Vacío = solo memoria.</summary>
    public string? StoragePath { get; set; } = "mishi.json";

    /// <summary>Si el nombre de un SP contiene alguno de estos textos, alterarlo es de riesgo alto.</summary>
    public List<string> CriticalNamePatterns { get; set; } = new() { "Cierre", "Clientes", "Saldo", "Transfer" };
}

public sealed record MishiProgress(int Xp, int Level, int LevelStartXp, int NextLevelXp, int KnownTables, int KnownPatterns, int Analyses);

/// <summary>Memoria de Mishi: experiencia, tablas conocidas, patrones aprendidos y consultas ya olfateadas.</summary>
public sealed class MishiMemory
{
    private sealed class Data
    {
        public int Xp { get; set; }
        public int Analyses { get; set; }
        public HashSet<string> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Patterns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Seen { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly MishiOptions _options;
    private Data _data = new();

    public MishiMemory(IOptions<MishiOptions> options)
    {
        _options = options.Value;
        Load();
    }

    public MishiOptions Options => _options;

    public static int LevelFor(int xp) => (int)Math.Floor(Math.Sqrt(Math.Max(0, xp) / 50.0)) + 1;

    public MishiProgress Progress()
    {
        lock (_gate) return ProgressLocked();
    }

    private MishiProgress ProgressLocked()
    {
        int level = LevelFor(_data.Xp);
        return new MishiProgress(_data.Xp, level, 50 * (level - 1) * (level - 1), 50 * level * level,
            _data.Tables.Count, _data.Patterns.Count, _data.Analyses);
    }

    /// <summary>Resultado de aprender de una consulta: lo que era nuevo y lo que ya se conocía.</summary>
    public sealed record Learned(
        bool FirstTimeSeen,
        IReadOnlyList<string> NewTables,
        IReadOnlyList<string> KnownTables,
        IReadOnlyList<string> NewPatterns,
        IReadOnlyList<string> KnownPatterns,
        MishiProgress Before,
        MishiProgress After);

    public Learned Learn(string fingerprint, IEnumerable<string> tables, IEnumerable<string> patterns, Func<Learned, int> xpFor)
    {
        lock (_gate)
        {
            MishiProgress before = ProgressLocked();
            bool first = _data.Seen.Add(fingerprint);
            List<string> newTables = new(), knownTables = new(), newPatterns = new(), knownPatterns = new();
            foreach (string table in tables) (_data.Tables.Add(table) ? newTables : knownTables).Add(table);
            foreach (string pattern in patterns) (_data.Patterns.Add(pattern) ? newPatterns : knownPatterns).Add(pattern);
            if (first) _data.Analyses++;

            Learned draft = new(first, newTables, knownTables, newPatterns, knownPatterns, before, before);
            _data.Xp += xpFor(draft);
            Learned result = draft with { After = ProgressLocked() };
            Save();
            return result;
        }
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_options.StoragePath)) return;
        try { File.WriteAllText(_options.StoragePath, JsonSerializer.Serialize(_data, Json)); }
        catch { /* la mascota nunca debe romper una respuesta */ }
    }

    private void Load()
    {
        if (string.IsNullOrWhiteSpace(_options.StoragePath) || !File.Exists(_options.StoragePath)) return;
        try
        {
            Data loaded = JsonSerializer.Deserialize<Data>(File.ReadAllText(_options.StoragePath), Json) ?? new Data();
            // Al deserializar se pierde el comparador: se reconstruyen los conjuntos sin distinguir mayúsculas.
            loaded.Tables = new HashSet<string>(loaded.Tables, StringComparer.OrdinalIgnoreCase);
            loaded.Patterns = new HashSet<string>(loaded.Patterns, StringComparer.OrdinalIgnoreCase);
            _data = loaded;
        }
        catch { _data = new Data(); }
    }
}
