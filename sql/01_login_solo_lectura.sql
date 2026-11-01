/* =============================================================================
   SqlGraphCopilot · 01 · Login de SOLO LECTURA DE METADATA
   -----------------------------------------------------------------------------
   Ejecutar como administrador en la instancia de DESARROLLO.
   El login puede ver definiciones y dependencias (sys.*), pero NO datos de negocio:
   no se otorga db_datareader ni SELECT sobre tablas.
   ============================================================================= */

USE master;
GO

/* Opción A: autenticación SQL (necesaria si la API corre en Docker/Linux) */
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'sqlgraph_reader')
    CREATE LOGIN [sqlgraph_reader] WITH PASSWORD = N'<<CAMBIAR_POR_UNA_CLAVE_FUERTE>>', CHECK_POLICY = ON;
GO

/* Opción B: cuenta de servicio de dominio (Windows)
   CREATE LOGIN [DOMINIO\svc_sqlgraph] FROM WINDOWS;
   -- y reemplace [sqlgraph_reader] por [DOMINIO\svc_sqlgraph] en el resto del script. */

/* Ver definiciones de todos los objetos de la instancia (SQL Server 2014+). */
GRANT VIEW ANY DEFINITION TO [sqlgraph_reader];
GO

/* ---------------------------------------------------------------------------
   Por cada base que se quiera analizar: crear el usuario (HAS_DBACCESS lo exige).
   Repita el bloque cambiando el nombre de la base, o genere los bloques con
   la consulta del final de este script.
   --------------------------------------------------------------------------- */
USE [NombreDeLaBase];
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'sqlgraph_reader')
    CREATE USER [sqlgraph_reader] FOR LOGIN [sqlgraph_reader];
GRANT VIEW DEFINITION TO [sqlgraph_reader];
GO

/* ---------------------------------------------------------------------------
   Generador: produce el bloque anterior para todas las bases de usuario ONLINE.
   Revise la salida antes de ejecutarla.
   --------------------------------------------------------------------------- */
USE master;
GO
SELECT
    N'USE ' + QUOTENAME(d.name) + N';' + CHAR(10) +
    N'IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N''sqlgraph_reader'') ' +
    N'CREATE USER [sqlgraph_reader] FOR LOGIN [sqlgraph_reader];' + CHAR(10) +
    N'GRANT VIEW DEFINITION TO [sqlgraph_reader];' + CHAR(10) + N'GO' AS Script
FROM sys.databases AS d
WHERE d.database_id > 4
  AND d.state = 0
ORDER BY d.name;
GO
