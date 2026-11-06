using SqlGraphCopilot.Application.Flow;

namespace SqlGraphCopilot.Application.Abstractions;

/// <summary>Análisis estático del flujo de un procedimiento almacenado (parámetros, ramas y validaciones).</summary>
public interface IProcedureFlowAnalyzer
{
    ProcedureFlowReport Analyze(string schema, string name, string definition);
}
