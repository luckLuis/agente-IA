namespace SqlGraphCopilot.Core.Domain;

public sealed record GraphNode
{
    public required ObjectKey Key { get; init; }
    public required NodeKind Kind { get; init; }
    public ExternalScope Scope { get; init; } = ExternalScope.None;
    public bool IsCritical { get; init; }

    /// <summary>Usa sp_executesql / EXEC(@sql): sus dependencias reales no son visibles en el catálogo.</summary>
    public bool HasDynamicSql { get; init; }

    /// <summary>Usa OPENQUERY hacia un servidor vinculado.</summary>
    public bool UsesOpenQuery { get; init; }

    /// <summary>Sus aristas ya fueron clasificadas (lee / escribe / ejecuta) con el parser T-SQL.</summary>
    public bool IsClassified { get; init; }

    public int? ObjectId { get; init; }
    public DateTime? ModifiedAt { get; init; }

    /// <summary>SHA-256 de la definición; permite detectar SPs duplicados sin exponer el código.</summary>
    public string? DefinitionHash { get; init; }

    public string Id => Key.Id;
    public string DisplayName => Key.DisplayName;
    public bool IsExternal => Scope != ExternalScope.None;
    public bool IsProgrammable => Kind is NodeKind.StoredProcedure or NodeKind.Function or NodeKind.Trigger or NodeKind.View;
}

public sealed record GraphEdge(string SourceId, string TargetId, DependencyKind Kinds)
{
    public bool Has(DependencyKind kind) => (Kinds & kind) != 0;
}
