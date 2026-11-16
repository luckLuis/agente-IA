namespace SqlGraphCopilot.Application.Flow;

public sealed record FlowParameter(string Name, string DataType, bool IsOptional, string? DefaultValue, bool IsOutput);

public sealed record ParamConstraint(string Parameter, string Operator, IReadOnlyList<string> Values);

public sealed record FlowCondition(
    string Text,
    IReadOnlyList<ParamConstraint> Constraints,
    bool Exact,
    IReadOnlyList<string> Parameters,
    bool DependsOnData);

public sealed record FlowArgument(string Name, string Value);

public sealed record FlowBranch(string Kind, string Target, IReadOnlyList<FlowCondition> Conditions, IReadOnlyList<FlowArgument> Arguments);

public sealed record FlowGuard(FlowCondition Condition, string Outcome);

public sealed record FlowStep(
    int Id,
    int ParentId,
    int Depth,
    string Kind,
    int Line,
    string Title,
    string? Operation,
    IReadOnlyList<string> Targets,
    IReadOnlyList<FlowCondition> Conditions,
    IReadOnlyList<FlowArgument> Arguments);

public sealed record ProcedureFlowReport(
    string Schema,
    string Name,
    IReadOnlyList<FlowParameter> Parameters,
    IReadOnlyList<FlowBranch> Branches,
    IReadOnlyList<FlowGuard> Guards,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<FlowStep> Steps);
