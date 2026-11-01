/* =============================================================================
   SqlGraphCopilot · 02 · Consultas de extracción (las mismas que ejecuta la API)
   -----------------------------------------------------------------------------
   Úselas para revisión con DBA / seguridad o para validar resultados a mano.
   Fuente en el código: src/SqlGraphCopilot.Core/Metadata/SqlCatalogQueries.cs
   Ninguna lee datos de negocio: solo vistas de catálogo sys.*
   ============================================================================= */

/* 1. Bases disponibles para el login (se ejecuta en master) */
SELECT d.name                AS DatabaseName,
       d.compatibility_level AS CompatibilityLevel
FROM sys.databases AS d
WHERE d.state = 0
  AND d.database_id > 4
  AND HAS_DBACCESS(d.name) = 1
ORDER BY d.name;

/* -------- Las siguientes se ejecutan dentro de la base seleccionada -------- */

/* 2. Objetos (+ pistas de SQL dinámico / OPENQUERY y huella SHA-256 de la definición).
      El código NO sale del servidor: solo el hash (para detectar duplicados).
      En SQL Server 2014 o anterior, HASHBYTES falla con definiciones de más de 8000 bytes:
      configure "ComputeDefinitionHash": false. */
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
       CONVERT(char(64), HASHBYTES('SHA2_256', m.definition), 2) AS DefinitionHash
FROM sys.objects AS o
LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('U','V','P','PC','X','FN','IF','TF','FS','FT','TR','TA','SN');

/* 3. Dependencias (incluye referencias de 3 y 4 partes: otra base / linked server) */
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
  AND d.referenced_entity_name NOT LIKE N'#%'
  AND d.referenced_entity_name NOT LIKE N'@%';

/* 4. Sinónimos (suelen ocultar dependencias cross-database) */
SELECT s.object_id                       AS ObjectId,
       PARSENAME(s.base_object_name, 4)  AS BaseServer,
       PARSENAME(s.base_object_name, 3)  AS BaseDatabase,
       PARSENAME(s.base_object_name, 2)  AS BaseSchema,
       PARSENAME(s.base_object_name, 1)  AS BaseName
FROM sys.synonyms AS s;

/* 5. Triggers DML y su tabla */
SELECT t.object_id AS TriggerId,
       t.parent_id AS ParentId
FROM sys.triggers AS t
WHERE t.parent_class = 1
  AND t.is_ms_shipped = 0;

/* 6. Definición de un objeto (bajo demanda, al expandir un nodo) */
DECLARE @objectId int = OBJECT_ID(N'dbo.NombreDelSP');
SELECT m.definition
FROM sys.sql_modules AS m
WHERE m.object_id = @objectId;

/* 7. Todas las definiciones (solo en el "escaneo profundo", en streaming) */
SELECT m.object_id  AS ObjectId,
       m.definition AS Definition
FROM sys.sql_modules AS m
INNER JOIN sys.objects AS o ON o.object_id = m.object_id
WHERE o.is_ms_shipped = 0
  AND m.definition IS NOT NULL
  AND o.type IN ('P','V','FN','IF','TF','TR');
