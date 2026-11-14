// Resaltado básico de T-SQL (sin dependencias). Devuelve HTML ya escapado, listo para un <pre><code>.
// Un solo recorrido con una expresión regular: comentarios, cadenas, variables, números y palabras clave.

const KEYWORDS = new Set(`ADD ALL ALTER AND ANY AS ASC BEGIN BETWEEN BREAK BY CASE CATCH CHECK CLOSE COMMIT COMMITTED CONSTRAINT
CONTINUE CREATE CROSS CURSOR DEALLOCATE DECLARE DEFAULT DELETE DESC DISTINCT DROP ELSE END EXCEPT EXEC EXECUTE EXISTS FETCH FOR
FOREIGN FROM FULL FUNCTION GO GOTO GROUP HAVING IDENTITY IF IN INDEX INNER INSERT INTERSECT INTO IS JOIN KEY LEFT LIKE MERGE NEXT
NOT NULL OFFSET ON ONLY OPEN OR ORDER OUT OUTER OUTPUT OVER PARTITION PRIMARY PRINT PROC PROCEDURE RAISERROR READ REFERENCES
RETURN RETURNS RIGHT ROLLBACK ROWS SELECT SET TABLE THEN THROW TOP TRAN TRANSACTION TRIGGER TRUNCATE TRY UNION UNIQUE UPDATE
USE VALUES VIEW WHEN WHERE WHILE WITH NOLOCK READUNCOMMITTED`.split(/\s+/));

const TYPES = new Set(`BIGINT BIT CHAR DATE DATETIME DATETIME2 DECIMAL FLOAT INT MONEY NCHAR NUMERIC NVARCHAR REAL SMALLINT
SYSNAME TINYINT UNIQUEIDENTIFIER VARBINARY VARCHAR XML`.split(/\s+/));

const TOKEN = /(--[^\n]*|\/\*[\s\S]*?\*\/)|('(?:[^']|'')*')|(@@?\w+)|(\b\d+(?:\.\d+)?\b)|(\[[^\]]+\])|(\b[A-Za-z_][\w$#]*\b)/g;

const escape = (text) => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

export function highlightSql(sql) {
  let html = '';
  let last = 0;
  for (const match of sql.matchAll(TOKEN)) {
    html += escape(sql.slice(last, match.index));
    const [text, comment, string, variable, number, bracket, word] = match;
    let cls = null;
    if (comment) cls = 'sql-comment';
    else if (string) cls = 'sql-string';
    else if (variable) cls = 'sql-var';
    else if (number) cls = 'sql-number';
    else if (bracket) cls = null;
    else if (word) {
      const upper = word.toUpperCase();
      if (KEYWORDS.has(upper)) cls = 'sql-kw';
      else if (TYPES.has(upper)) cls = 'sql-type';
    }
    html += cls ? `<span class="${cls}">${escape(text)}</span>` : escape(text);
    last = match.index + text.length;
  }
  return html + escape(sql.slice(last));
}
