using System.Globalization;
using System.Text;

namespace SqlGraphCopilot.Application.Agent;

/// <summary>
/// Detecta la intención por palabras clave (español/inglés, sin tildes).
/// Es determinista a propósito: la decisión de QUÉ análisis correr no depende del LLM,
/// así el resultado es reproducible y auditable.
/// </summary>
public sealed class IntentClassifier
{
    private static readonly (AgentIntent Intent, string[] Keywords)[] Rules =
    {
        (AgentIntent.Help, new[] { "ayuda", "help", "que puedes", "que sabes hacer", "como funciona", "como te uso" }),
        (AgentIntent.BrokenReferences, new[] { "rotas", "rotos", "referencia rota", "objeto roto", "inexistente", "no existe", "huerfan", "broken" }),
        (AgentIntent.Redundancy, new[] { "redundan", "duplicad", "repetid", "similar", "parecid", "copia de", "codigo duplicado" }),
        (AgentIntent.Bottlenecks, new[] { "cuello", "botella", "bottleneck", "lento", "lentitud", "rendimiento", "performance", "bloqueo", "contencion", "deadlock", "concurrencia", "sql dinamico" }),
        (AgentIntent.SinglePointsOfFailure, new[] { "spof", "punto unico", "puntos unicos", "single point", "punto de fallo", "puntos de fallo", "puntos criticos", "punto critico" }),
        (AgentIntent.Coupling, new[] { "acopla", "coupling", "fan-in", "fan in", "fanin", "mas usad", "mas referenciad", "mas dependencias" }),
        (AgentIntent.Impact, new[] { "impacto", "modific", "cambi", "rompe", "romper", "afecta", "afectar", "borr", "elimin", "drop", "alter", "renombr", "que pasa si", "blast", "riesgo" }),
        (AgentIntent.Explain, new[] { "que es", "que hace", "describe", "explica", "detalle", "dependencias de", "de que depende", "quien usa", "quien llama", "quienes usan", "donde se usa" }),
        (AgentIntent.Overview, new[] { "resumen", "general", "salud", "estado de la base", "overview", "diagnostico", "panorama", "analiza la base", "analisis completo" }),
        (AgentIntent.MigrateToCode, new[] { "migra", "migrar", "a c#", "a csharp", "pasar a codigo", "pasa a codigo", "mover a codigo", "mueve a codigo", "convierte", "convertir", "reescribe", "dame el codigo", "codigo c#", "dapper", "ef core", "entity framework" }),
        (AgentIntent.ExplainLogic, new[] { "logica", "paso a paso", "por dentro", "que hace por dentro", "explica el codigo", "explicame el codigo", "analiza el codigo", "analiza el sp", "revisa el sp" }),
    };

    /// <summary>Devuelve la intención o null si ninguna regla aplica (el agente decide según haya o no un objeto).</summary>
    public AgentIntent? Classify(string message)
    {
        string normalized = Normalize(message);
        foreach ((AgentIntent intent, string[] keywords) in Rules)
        {
            if (keywords.Any(k => normalized.Contains(k, StringComparison.Ordinal)))
            {
                return intent;
            }
        }
        return null;
    }

    public static string Normalize(string text)
    {
        string decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        StringBuilder builder = new(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
