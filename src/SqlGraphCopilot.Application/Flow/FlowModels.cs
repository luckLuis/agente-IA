namespace SqlGraphCopilot.Application.Flow;

/// <summary>Parámetro del procedimiento.</summary>
public sealed record FlowParameter(string Name, string DataType, bool IsOptional, string? DefaultValue, bool IsOutput);

/// <summary>Restricción simple sobre un parámetro. Operator: =, &lt;&gt;, &gt;, &gt;=, &lt;, &lt;=, IS NULL, IS NOT NULL, IN, NOT IN.</summary>
public sealed record ParamConstraint(string Parameter, string Operator, IReadOnlyList<string> Values);

/// <summary>
/// Condición que protege una acción. Constraints son las partes que se pueden evaluar con valores de entrada;
/// Exact = true cuando Constraints representa toda la condición. DependsOnData: usa tablas, variables locales o funciones.
/// </summary>
public sealed record FlowCondition(
    string Text,
    IReadOnlyList<ParamConstraint> Constraints,
    bool Exact,
    IReadOnlyList<string> Parameters,
    bool DependsOnData);

public sealed record FlowArgument(string Name, string Value);

/// <summary>Acción dentro del SP. Kind: exec | write | dynamic.</summary>
public sealed record FlowBranch(string Kind, string Target, IReadOnlyList<FlowCondition> Conditions, IReadOnlyList<FlowArgument> Arguments);

/// <summary>Validación que corta el flujo (RETURN / THROW) cuando se cumple la condición.</summary>
public sealed record FlowGuard(FlowCondition Condition, string Outcome);

public sealed record ProcedureFlowReport(
    string Schema,
    string Name,
    IReadOnlyList<FlowParameter> Parameters,
    IReadOnlyList<FlowBranch> Branches,
    IReadOnlyList<FlowGuard> Guards,
    IReadOnlyList<string> Warnings);
