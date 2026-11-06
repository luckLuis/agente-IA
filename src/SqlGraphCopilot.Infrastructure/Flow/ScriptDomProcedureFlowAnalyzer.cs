using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Flow;

namespace SqlGraphCopilot.Infrastructure.Flow;

/// <summary>Adaptador del analizador estático (ScriptDom) al puerto de Application.</summary>
public sealed class ScriptDomProcedureFlowAnalyzer : IProcedureFlowAnalyzer
{
    public ProcedureFlowReport Analyze(string schema, string name, string definition) =>
        ProcedureFlowAnalyzer.Analyze(schema, name, definition);
}
