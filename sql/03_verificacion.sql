/* =============================================================================
   SqlGraphCopilot · 03 · Verificación (ejecutar CONECTADO COMO sqlgraph_reader)
   -----------------------------------------------------------------------------
   Confirma que el login ve la metadata y estima el volumen antes de usar la API.
   ============================================================================= */

/* 1. ¿Tiene el permiso de servidor? (1 = sí) */
SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW ANY DEFINITION') AS TieneViewAnyDefinition;

/* 2. ¿A qué bases puede entrar? (deben aparecer las que va a analizar) */
SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name;

/* ---- Lo siguiente, dentro de cada base a analizar ---- */
-- USE [NombreDeLaBase];

/* 3. Volumen por tipo (si las definiciones salen NULL, falta VIEW DEFINITION) */
SELECT RTRIM(o.type)                                   AS Tipo,
       COUNT(*)                                        AS Objetos,
       SUM(CASE WHEN m.definition IS NULL AND o.type <> 'U' AND o.type <> 'SN' THEN 1 ELSE 0 END) AS SinDefinicionVisible
FROM sys.objects AS o
LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('U','V','P','PC','X','FN','IF','TF','FS','FT','TR','TA','SN')
GROUP BY o.type
ORDER BY Objetos DESC;

/* 4. Dependencias registradas, y cuántas apuntan a otra base o servidor */
SELECT COUNT(*)                                                         AS Dependencias,
       SUM(CASE WHEN referenced_database_name IS NOT NULL THEN 1 ELSE 0 END) AS HaciaOtraBase,
       SUM(CASE WHEN referenced_server_name   IS NOT NULL THEN 1 ELSE 0 END) AS HaciaLinkedServer,
       SUM(CASE WHEN referenced_id IS NULL AND referenced_database_name IS NULL THEN 1 ELSE 0 END) AS NoResueltas
FROM sys.sql_expression_dependencies
WHERE referencing_class = 1 AND referenced_class = 1;

/* 5. Linked servers definidos en la instancia: agregue sus nombres a LinkedServerAliases */
SELECT name AS LinkedServer, data_source AS Origen, product AS Producto
FROM sys.servers
WHERE is_linked = 1;

/* 6. Nombre real del servidor (agréguelo a LinkedServerAliases de esta instancia) */
SELECT @@SERVERNAME AS ServerName;
