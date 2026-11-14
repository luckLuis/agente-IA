using SqlGraphCopilot.Domain;

namespace SqlGraphCopilot.Application.Catalog;

public sealed record DatabaseInfo(string Name, int CompatibilityLevel);

/// <summary>Fila de sys.objects (+ pistas baratas calculadas en el servidor sin traer el código).</summary>
public sealed record CatalogObject(
    int ObjectId,
    string Schema,
    string Name,
    string TypeCode,
    DateTime ModifiedAt,
    bool HasDynamicSqlHint,
    bool UsesOpenQueryHint,
    string? DefinitionHash);

/// <summary>Fila de sys.sql_expression_dependencies.</summary>
public sealed record CatalogDependency(
    int ReferencingId,
    string? ReferencedServer,
    string? ReferencedDatabase,
    string? ReferencedSchema,
    string ReferencedName,
    int? ReferencedId,
    bool IsCallerDependent,
    bool IsAmbiguous);

public sealed record CatalogSynonym(int ObjectId, string? BaseServer, string? BaseDatabase, string? BaseSchema, string BaseName);

public sealed record CatalogTrigger(int TriggerId, int ParentId);

public sealed record CatalogSnapshot(
    string Instance,
    string Database,
    IReadOnlyList<CatalogObject> Objects,
    IReadOnlyList<CatalogDependency> Dependencies,
    IReadOnlyList<CatalogSynonym> Synonyms,
    IReadOnlyList<CatalogTrigger> Triggers);

public sealed record ObjectDefinition(int ObjectId, string Definition);

public static class CatalogTypeMapper
{
    /// <summary>Traduce sys.objects.type a <see cref="NodeKind"/>. Null = tipo que no se modela.</summary>
    public static NodeKind? ToNodeKind(string typeCode) => typeCode.Trim().ToUpperInvariant() switch
    {
        "U" => NodeKind.Table,
        "V" => NodeKind.View,
        "P" or "PC" or "X" => NodeKind.StoredProcedure,
        "FN" or "IF" or "TF" or "FS" or "FT" => NodeKind.Function,
        "TR" or "TA" => NodeKind.Trigger,
        "SN" => NodeKind.Synonym,
        _ => null
    };
}
