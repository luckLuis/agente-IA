using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SqlGraphCopilot.Core.Mishi;

/// <summary>Respuesta de Mishi-SQL. Los cinco primeros campos son el contrato del agente; el resto alimenta la interfaz.</summary>
public sealed record MishiReply(
    [property: JsonPropertyName("cat_state")] string CatState,
    [property: JsonPropertyName("bubble_text")] string BubbleText,
    [property: JsonPropertyName("gained_xp")] int GainedXp,
    [property: JsonPropertyName("learned_pattern")] string? LearnedPattern,
    [property: JsonPropertyName("technical_response")] string TechnicalResponse,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("total_xp")] int TotalXp,
    [property: JsonPropertyName("xp_level_start")] int XpLevelStart,
    [property: JsonPropertyName("xp_next_level")] int XpNextLevel,
    [property: JsonPropertyName("level_up")] bool LevelUp,
    [property: JsonPropertyName("findings")] IReadOnlyList<RiskFinding> Findings);

/// <summary>
/// Mishi-SQL: decide el estado de ánimo, la experiencia y el mensaje del gatito a partir del análisis AST.
/// Cero tokens: nada de esto llama a un LLM.
/// Estados: alert (riesgo alto) → studying (tablas nuevas) → smart (reconoce tablas o patrones ya aprendidos) → happy (seguro).
/// XP: +10 por analizar algo nuevo, +25 por un patrón de riesgo nuevo, +50 por sugerir una optimización nueva.
/// </summary>
public sealed class MishiService
{
    private readonly MishiMemory _memory;

    public MishiService(MishiMemory memory)
    {
        _memory = memory;
    }

    public MishiProgress Progress() => _memory.Progress();

    public MishiReply Analyze(string sql, string? objectName = null)
    {
        ScanResult scan = RiskScanner.Scan(sql, _memory.Options.CriticalNamePatterns);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Trim())));

        string PatternKey(RiskFinding f) => $"{f.Id}|{f.Table}";
        MishiMemory.Learned learned = _memory.Learn(
            fingerprint,
            scan.Tables,
            scan.Findings.Select(PatternKey),
            draft =>
            {
                int xp = draft.FirstTimeSeen ? 10 : 0;
                bool newRisk = scan.Findings.Any(f => !f.IsOptimization && draft.NewPatterns.Contains(PatternKey(f)));
                bool newTip = scan.Findings.Any(f => f.IsOptimization && draft.NewPatterns.Contains(PatternKey(f)));
                return xp + (newRisk ? 25 : 0) + (newTip ? 50 : 0);
            });

        bool high = scan.Findings.Any(f => f.Severity == "high");
        bool recognized = scan.Findings.Any(f => learned.KnownPatterns.Contains(PatternKey(f)))
            || (scan.Tables.Count > 0 && learned.NewTables.Count == 0 && scan.Findings.Count > 0);

        string state = high ? "alert"
            : learned.NewTables.Count > 0 ? "studying"
            : recognized ? "smart"
            : "happy";

        int gained = learned.After.Xp - learned.Before.Xp;
        string label = objectName ?? "esta consulta";
        return new MishiReply(
            state,
            Bubble(state, label, scan, learned, fingerprint),
            gained,
            LearnedText(scan, learned, PatternKey),
            Technical(label, scan, learned),
            learned.After.Level,
            learned.After.Xp,
            learned.After.LevelStartXp,
            learned.After.NextLevelXp,
            learned.After.Level > learned.Before.Level,
            scan.Findings);
    }

    // ---- Texto -----------------------------------------------------------------------------

    private static string Pick(string seed, params string[] options) =>
        options[(int)(Convert.ToUInt32(seed[..6], 16) % (uint)options.Length)];

    private static string Bubble(string state, string label, ScanResult scan, MishiMemory.Learned learned, string seed)
    {
        RiskFinding? top = scan.Findings.FirstOrDefault();
        string table = top?.Table ?? label;
        switch (state)
        {
            case "alert":
                return top!.Id switch
                {
                    "update-sin-where" => $"¡Miau! 🙀 Vas a actualizar {table} sin WHERE. ¡Se me erizó el pelaje!",
                    "delete-sin-where" => $"¡Miau! 🙀 Un DELETE sin WHERE en {table}… ¡saco las garras antes de que borres todo!",
                    "truncate" => $"¡Miau! 🙀 TRUNCATE en {table}: eso no tiene vuelta atrás, humano.",
                    "drop" => $"¡Miau! 🙀 Un DROP sobre {table}. ¡Me tiemblan hasta los bigotes!",
                    "alter-critico" => $"¡Miau! 🙀 Vas a alterar {table}, un SP crítico. ¡Guarda un respaldo antes de afilar las garras!",
                    _ => $"¡Miau! 🙀 Huelo un riesgo alto en {label}. ¡Ojo!"
                };
            case "studying":
                return $"Mrrr… 📚 Es la primera vez que olfateo {Join(learned.NewTables)}. ¡Estoy tomando notas!";
            case "smart":
                return learned.KnownTables.Count > 0
                    ? Pick(seed, $"¡Miau! 😼 Esto me suena: ya conozco {Join(learned.KnownTables)}.", $"Mis bigotes no fallan: {Join(learned.KnownTables)} ya estaba en mi libreta. 😼")
                    : "¡Miau! 😼 Este patrón ya lo tenía anotado.";
            default:
                bool tips = scan.Findings.Any(f => f.IsOptimization);
                return tips
                    ? "¡Purrr! 😸 Está sano, pero podría correr más ligero. Mira mis consejos."
                    : Pick(seed, "¡Purrr! 😸 Todo limpio: no huelo ningún riesgo.", "¡Purrr! 😸 Esta consulta es segura. Me estiro tranquilo.");
        }
    }

    private static string Join(IEnumerable<string> items)
    {
        List<string> list = items.Take(3).ToList();
        int extra = items.Count() - list.Count;
        return string.Join(", ", list) + (extra > 0 ? $" y {extra} más" : string.Empty);
    }

    private static string? LearnedText(ScanResult scan, MishiMemory.Learned learned, Func<RiskFinding, string> key)
    {
        List<string> parts = new();
        foreach (RiskFinding f in scan.Findings.Where(f => learned.NewPatterns.Contains(key(f))))
        {
            parts.Add($"{(f.IsOptimization ? "Optimización" : "Patrón de riesgo")}: {f.Title}{(f.Table is null ? string.Empty : $" en {f.Table}")}.");
        }
        if (learned.NewTables.Count > 0) parts.Add($"Tablas aprendidas: {string.Join(", ", learned.NewTables)}.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static string Technical(string label, ScanResult scan, MishiMemory.Learned learned)
    {
        StringBuilder sb = new();
        if (scan.Findings.Count == 0)
        {
            sb.AppendLine($"Sin riesgos detectados en {label}.");
        }
        else
        {
            int high = scan.Findings.Count(f => f.Severity == "high");
            sb.AppendLine(high > 0
                ? $"**Riesgo alto** en {label}: {high} hallazgo(s) crítico(s)."
                : $"Sin riesgos altos en {label}; hay {scan.Findings.Count} punto(s) a mejorar.");
            sb.AppendLine();
            foreach (RiskFinding f in scan.Findings)
            {
                string icon = f.Severity switch { "high" => "🔴", "medium" => "🟠", _ => "🟡" };
                sb.AppendLine($"- {icon} **{f.Title}**{(f.Table is null ? string.Empty : $" · `{f.Table}`")}: {f.Detail} _Recomendación:_ {f.Recommendation}");
            }
        }
        if (scan.Tables.Count > 0)
        {
            sb.AppendLine().AppendLine($"**Tablas que toca:** {string.Join(", ", scan.Tables.Select(t => $"`{t}`"))}.");
        }
        if (!learned.FirstTimeSeen) sb.AppendLine().AppendLine("_Ya había olfateado esta consulta: no suma experiencia nueva._");
        if (scan.ParseErrors.Count > 0)
        {
            sb.AppendLine().AppendLine($"_El analizador tuvo problemas de sintaxis; el resultado puede estar incompleto ({scan.ParseErrors[0]})._");
        }
        return sb.ToString().TrimEnd();
    }
}
