using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlGraphCopilot.Core.Parsing;

public interface ITSqlParsingService
{
    /// <summary>
    /// Analiza un script T-SQL. No lanza excepción por errores de sintaxis:
    /// los devuelve en <see cref="SqlAnalysisResult.Errors"/> junto con lo que se pudo extraer.
    /// </summary>
    SqlAnalysisResult ParseSqlScript(string sqlScript);
}

public sealed class TSqlParsingService : ITSqlParsingService
{
    public SqlAnalysisResult ParseSqlScript(string sqlScript)
    {
        if (string.IsNullOrWhiteSpace(sqlScript))
        {
            return Empty(new[] { "El script está vacío." });
        }

        using StringReader reader = new(sqlScript);
        TSql160Parser parser = new(initialQuotedIdentifiers: true);
        TSqlFragment? fragment = parser.Parse(reader, out IList<ParseError> parseErrors);

        List<string> errors = parseErrors
            .Select(e => $"Línea {e.Line}, columna {e.Column}: {e.Message}")
            .ToList();

        if (fragment is null)
        {
            return Empty(errors);
        }

        SqlAstVisitor visitor = new();
        fragment.Accept(visitor);
        return visitor.BuildResult(errors);
    }

    private static SqlAnalysisResult Empty(IReadOnlyList<string> errors) => new(
        null,
        Array.Empty<SqlObjectName>(),
        Array.Empty<SqlObjectName>(),
        Array.Empty<SqlObjectName>(),
        Array.Empty<SqlObjectName>(),
        Array.Empty<string>(),
        false,
        false,
        errors);
}
