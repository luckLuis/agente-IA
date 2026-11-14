namespace SqlGraphCopilot.Application.Parsing;

/// <summary>Nombre de objeto tal como aparece en el código (de 1 a 4 partes).</summary>
public sealed record SqlObjectName(string? Server, string? Database, string? Schema, string Name)
{
    public bool IsQualified => Schema is not null || Database is not null || Server is not null;

    public override string ToString() =>
        string.Join('.', new[] { Server, Database, Schema, Name }.Where(p => p is not null));
}

/// <summary>Resultado del análisis estático de un módulo T-SQL (SP, vista, función o trigger).</summary>
public sealed record SqlAnalysisResult(
    string? ObjectName,
    IReadOnlyList<SqlObjectName> Reads,
    IReadOnlyList<SqlObjectName> Writes,
    IReadOnlyList<SqlObjectName> Executes,
    IReadOnlyList<SqlObjectName> Calls,
    IReadOnlyList<string> LinkedServers,
    bool HasDynamicSql,
    bool UsesOpenQuery,
    IReadOnlyList<string> Errors)
{
    public bool HasErrors => Errors.Count > 0;
}
