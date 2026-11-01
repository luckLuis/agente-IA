using System.Text.RegularExpressions;
using SqlGraphCopilot.Core.Domain;
using SqlGraphCopilot.Core.Indexing;

namespace SqlGraphCopilot.Core.Agent;

public sealed record EntityResolution(IReadOnlyList<GraphNode> Matches, bool UsedFocus);

/// <summary>
/// Encuentra los objetos que nombra la pregunta buscando en el índice REAL de la base
/// (el original solo buscaba en los nodos ya cargados y caía en "dbo" si no encontraba nada).
/// </summary>
public sealed partial class EntityResolver
{
    private const int MaxEntities = 3;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "que", "qué", "pasa", "si", "modifico", "modificar", "cambio", "cambiar", "el", "la", "los", "las", "de", "del",
        "un", "una", "tabla", "sp", "procedimiento", "vista", "funcion", "función", "sobre", "con", "para", "por",
        "cual", "cuál", "cuales", "es", "son", "hay", "mi", "en", "al", "se", "me", "quien", "quién", "usa", "usan",
        "depende", "dependen", "impacto", "afecta", "rompe", "base", "datos", "como", "cómo", "donde", "dónde",
        "este", "esta", "esto", "borro", "elimino", "the", "what", "if", "change", "table", "procedure", "view"
    };

    private static readonly Regex DeicticRegex = new(@"\b(este|esta|esto|ese|esa|this|selected)\b|\bseleccionad", RegexOptions.CultureInvariant);

    public EntityResolution Resolve(DatabaseIndex index, string message, string? focusNodeId)
    {
        GraphNode? focus = focusNodeId is null ? null : index.Graph.GetNode(focusNodeId);
        string normalized = IntentClassifier.Normalize(message);
        bool deictic = DeicticRegex.IsMatch(normalized);

        List<GraphNode> matches = new();
        foreach (string token in Tokens(message))
        {
            if (matches.Count >= MaxEntities)
            {
                break;
            }

            GraphNode? match = index.FindByName(token).FirstOrDefault();
            if (match is null && LooksLikeIdentifier(token))
            {
                // Coincidencia parcial solo para tokens con forma de identificador (sp_X, dbo.Y, TablaCamelCase).
                match = index.Search(new ObjectSearchQuery(token, null, false, 1, 1)).Items.FirstOrDefault();
            }

            if (match is not null && !matches.Any(m => m.Id == match.Id))
            {
                matches.Add(match);
            }
        }

        if (focus is not null && (matches.Count == 0 || deictic))
        {
            matches.RemoveAll(m => m.Id == focus.Id);
            matches.Insert(0, focus);
            return new EntityResolution(matches.Take(MaxEntities).ToList(), true);
        }

        return new EntityResolution(matches, false);
    }

    private static IEnumerable<string> Tokens(string message)
    {
        foreach (Match match in TokenRegex().Matches(message))
        {
            string token = match.Value.Trim('.', ',', ';', ':', '?', '¿', '!', '¡', '"', '\'', '`');
            if (token.Length >= 3 && !StopWords.Contains(token))
            {
                yield return token;
            }
        }
    }

    private static bool LooksLikeIdentifier(string token) =>
        token.Contains('_') || token.Contains('.') || token.Any(char.IsDigit) ||
        (token.Length > 1 && token.Skip(1).Any(char.IsUpper));

    [GeneratedRegex(@"[\[\]\w#@$.]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
