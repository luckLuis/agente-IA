namespace SqlGraphCopilot.Core.Agent;

public enum AgentIntent
{
    Impact,
    Explain,
    ExplainLogic,     // nuevo
    MigrateToCode,    // nuevo
    SinglePointsOfFailure,
    Coupling,
    Redundancy,
    Bottlenecks,
    BrokenReferences,
    Overview,
    Help
}

/// <param name="FocusNodeId">Nodo seleccionado en el grafo; se usa si la pregunta dice "este", "esto" o no nombra un objeto.</param>
public sealed record AgentRequest(string Instance, string Database, string Message, string? FocusNodeId);

/// <param name="Reply">Respuesta en Markdown.</param>
/// <param name="FocusNodeIds">Nodos que el frontend debe resaltar.</param>
/// <param name="Facts">Hechos estructurados que respaldan la respuesta (lo mismo que recibe el LLM).</param>
/// <param name="Engine">"deterministic" o el proveedor LLM usado.</param>
public sealed record AgentResponse(
    string Reply,
    AgentIntent Intent,
    IReadOnlyList<string> FocusNodeIds,
    object? Facts,
    string Engine,
    IReadOnlyList<string> Warnings);
