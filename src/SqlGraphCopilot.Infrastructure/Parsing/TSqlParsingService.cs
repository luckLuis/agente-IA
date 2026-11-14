using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Parsing;

namespace SqlGraphCopilot.Infrastructure.Parsing;

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
