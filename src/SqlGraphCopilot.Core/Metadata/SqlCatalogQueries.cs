namespace SqlGraphCopilot.Core.Metadata;

/// <summary>
/// Consultas de extracción. Solo leen vistas de catálogo (sys.*): nunca datos de negocio.
/// Requieren un login con VIEW DEFINITION (ver sql/01_login_solo_lectura.sql).
/// </summary>
internal static class SqlCatalogQueries
{
    public const string ListDatabases = """
        SELECT d.name                AS DatabaseName,
               d.compatibility_level AS CompatibilityLevel
        FROM sys.databases AS d
        WHERE d.state = 0                 -- ONLINE
          AND d.database_id > 4           -- excluye master, tempdb, model, msdb
          AND HAS_DBACCESS(d.name) = 1    -- solo bases a las que el login tiene acceso
        ORDER BY d.name;
        """;

    private const string ObjectsTemplate = """
        SELECT o.object_id                AS ObjectId,
               SCHEMA_NAME(o.schema_id)   AS SchemaName,
               o.name                     AS ObjectName,
               RTRIM(o.type)              AS TypeCode,
               o.modify_date              AS ModifiedAt,
               CAST(CASE WHEN m.definition LIKE N'%sp[_]executesql%'
                           OR m.definition LIKE N'%EXEC(%'
                           OR m.definition LIKE N'%EXEC (%'
                           OR m.definition LIKE N'%EXECUTE(%'
                           OR m.definition LIKE N'%EXECUTE (%'
                         THEN 1 ELSE 0 END AS bit) AS HasDynamicSqlHint,
               CAST(CASE WHEN m.definition LIKE N'%OPENQUERY%' THEN 1 ELSE 0 END AS bit) AS UsesOpenQueryHint,
               {0}                        AS DefinitionHash
        FROM sys.objects AS o
        LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
        WHERE o.is_ms_shipped = 0
          AND o.type IN ('U','V','P','PC','X','FN','IF','TF','FS','FT','TR','TA','SN');
        """;

    // El código nunca sale del servidor: solo su huella SHA-256 (para detectar duplicados).
    public static readonly string ObjectsWithHash =
        string.Format(ObjectsTemplate, "CONVERT(char(64), HASHBYTES('SHA2_256', m.definition), 2)");

    public static readonly string ObjectsWithoutHash =
        string.Format(ObjectsTemplate, "CAST(NULL AS char(64))");

    public const string Dependencies = """
        SELECT d.referencing_id            AS ReferencingId,
               d.referenced_server_name    AS ReferencedServer,
               d.referenced_database_name  AS ReferencedDatabase,
               COALESCE(d.referenced_schema_name, OBJECT_SCHEMA_NAME(d.referenced_id)) AS ReferencedSchema,
               d.referenced_entity_name    AS ReferencedName,
               d.referenced_id             AS ReferencedId,
               d.is_caller_dependent       AS IsCallerDependent,
               d.is_ambiguous              AS IsAmbiguous
        FROM sys.sql_expression_dependencies AS d
        INNER JOIN sys.objects AS src
                ON src.object_id = d.referencing_id
               AND src.is_ms_shipped = 0
        WHERE d.referencing_class = 1
          AND d.referenced_class = 1
          AND d.referenced_entity_name IS NOT NULL
          AND d.referenced_entity_name NOT LIKE N'#%'   -- tablas temporales
          AND d.referenced_entity_name NOT LIKE N'@%';  -- variables de tabla
        """;

    public const string Synonyms = """
        SELECT s.object_id                       AS ObjectId,
               PARSENAME(s.base_object_name, 4)  AS BaseServer,
               PARSENAME(s.base_object_name, 3)  AS BaseDatabase,
               PARSENAME(s.base_object_name, 2)  AS BaseSchema,
               PARSENAME(s.base_object_name, 1)  AS BaseName
        FROM sys.synonyms AS s;
        """;

    public const string Triggers = """
        SELECT t.object_id AS TriggerId,
               t.parent_id AS ParentId
        FROM sys.triggers AS t
        WHERE t.parent_class = 1          -- triggers DML sobre tablas/vistas
          AND t.is_ms_shipped = 0;
        """;

    public const string Definition = """
        SELECT m.definition
        FROM sys.sql_modules AS m
        WHERE m.object_id = @objectId;
        """;

    public const string AllDefinitions = """
        SELECT m.object_id  AS ObjectId,
               m.definition AS Definition
        FROM sys.sql_modules AS m
        INNER JOIN sys.objects AS o ON o.object_id = m.object_id
        WHERE o.is_ms_shipped = 0
          AND m.definition IS NOT NULL
          AND o.type IN ('P','V','FN','IF','TF','TR');
        """;
}
