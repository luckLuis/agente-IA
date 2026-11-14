using SqlGraphCopilot.Application.Parsing;

namespace SqlGraphCopilot.Application.Abstractions;

public interface ITSqlParsingService
{
    /// <summary>
    /// Analiza un script T-SQL. No lanza excepción por errores de sintaxis:
    /// los devuelve en <see cref="SqlAnalysisResult.Errors"/> junto con lo que se pudo extraer.
    /// </summary>
    SqlAnalysisResult ParseSqlScript(string sqlScript);
}
