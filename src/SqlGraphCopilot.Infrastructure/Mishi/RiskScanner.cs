using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Mishi;

namespace SqlGraphCopilot.Infrastructure.Mishi;

public sealed class RiskScanner : IRiskScanner
{
    public ScanResult Scan(string sql, IReadOnlyList<string> criticalNamePatterns)
    {
        TSql160Parser parser = new(initialQuotedIdentifiers: true);
        using StringReader reader = new(sql);
        TSqlFragment? fragment = parser.Parse(reader, out IList<ParseError> errors);

        List<string> parseErrors = errors.Take(3).Select(e => $"línea {e.Line}: {e.Message}").ToList();
        if (fragment is null) return new ScanResult([], [], parseErrors);

        Collector collector = new(criticalNamePatterns);
        fragment.Accept(collector);
        return new ScanResult(collector.Findings(), collector.Tables.Order(StringComparer.OrdinalIgnoreCase).ToList(), parseErrors);
    }

    private sealed class Collector : TSqlFragmentVisitor
    {
        private readonly IReadOnlyList<string> _critical;
        private readonly Dictionary<string, RiskFinding> _findings = new(StringComparer.OrdinalIgnoreCase);
        private bool _beginTran;
        private bool _tryCatch;
        private bool _noCount;
        private bool _isProcedure;

        public Collector(IReadOnlyList<string> critical) { _critical = critical; }

        public HashSet<string> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<RiskFinding> Findings()
        {
            if (_beginTran && !_tryCatch)
            {
                Add(new RiskFinding("tran-sin-trycatch", "medium", "Transacción sin TRY/CATCH",
                    "Hay BEGIN TRAN pero ningún bloque TRY/CATCH: ante un error la transacción puede quedar abierta y bloquear otras sesiones.",
                    "Envuelva la transacción en TRY/CATCH y haga ROLLBACK en el CATCH (con SET XACT_ABORT ON).", null, false));
            }
            if (_isProcedure && !_noCount)
            {
                Add(new RiskFinding("sin-nocount", "low", "Falta SET NOCOUNT ON",
                    "Sin NOCOUNT el servidor envía un mensaje de filas afectadas por cada sentencia: más tráfico y más lento.",
                    "Agregue SET NOCOUNT ON al inicio del procedimiento.", null, true));
            }
            return _findings.Values.OrderBy(f => f.Severity switch { "high" => 0, "medium" => 1, _ => 2 }).ToList();
        }

        private void Add(RiskFinding finding) => _findings.TryAdd($"{finding.Id}|{finding.Table}", finding);

        private static string? Name(TableReference? table) =>
            table is NamedTableReference named ? Name(named.SchemaObject) : null;

        private static string Name(SchemaObjectName name) => $"{name.SchemaIdentifier?.Value ?? "dbo"}.{name.BaseIdentifier.Value}";

        private static bool IsTemporary(string? name) => name is null || name.Contains('#') || name.StartsWith('@');

        public override void Visit(NamedTableReference node)
        {
            string name = Name(node.SchemaObject);
            if (!IsTemporary(name)) Tables.Add(name);
        }

        public override void Visit(UpdateSpecification node)
        {
            string? table = Name(node.Target);
            if (node.WhereClause is null && !IsTemporary(table))
            {
                Add(new RiskFinding("update-sin-where", "high", "UPDATE sin WHERE",
                    $"Modificará el 100 % de las filas de {table}.",
                    "Agregue un predicado WHERE (o confirme que es intencional y pruebe en una transacción con ROLLBACK).", table, false));
            }
        }

        public override void Visit(DeleteSpecification node)
        {
            string? table = Name(node.Target);
            if (node.WhereClause is null && !IsTemporary(table))
            {
                Add(new RiskFinding("delete-sin-where", "high", "DELETE sin WHERE",
                    $"Borrará todas las filas de {table}.",
                    "Agregue un WHERE; si quiere vaciar la tabla a propósito, hágalo explícito y con respaldo.", table, false));
            }
        }

        public override void Visit(TruncateTableStatement node)
        {
            string table = Name(node.TableName);
            if (IsTemporary(table)) return;
            Add(new RiskFinding("truncate", "high", "TRUNCATE TABLE",
                $"Vacía {table} de golpe, sin registro fila a fila y sin poder filtrar.",
                "Confirme que es intencional, verifique claves foráneas y tenga un respaldo.", table, false));
        }

        public override void Visit(DropTableStatement node)
        {
            foreach (SchemaObjectName obj in node.Objects)
            {
                string table = Name(obj);
                if (IsTemporary(table)) continue;
                Add(new RiskFinding("drop", "high", "DROP TABLE", $"Elimina {table} y todos sus datos.",
                    "Revise dependencias con el grafo y respalde antes de aplicar.", table, false));
            }
        }

        public override void Visit(DropProcedureStatement node)
        {
            foreach (SchemaObjectName obj in node.Objects)
            {
                Add(new RiskFinding("drop", "high", "DROP PROCEDURE", $"Elimina {Name(obj)}: quien lo llame fallará.",
                    "Use «Analizar impacto» para ver quién depende de él antes de borrarlo.", Name(obj), false));
            }
        }

        public override void Visit(DropDatabaseStatement node)
        {
            Add(new RiskFinding("drop", "high", "DROP DATABASE", "Elimina una base de datos completa.",
                "Casi nunca debe estar en un script de aplicación.", null, false));
        }

        public override void Visit(AlterProcedureStatement node)
        {
            _isProcedure = true;
            string name = Name(node.ProcedureReference.Name);
            if (_critical.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                Add(new RiskFinding("alter-critico", "high", "ALTER de un SP crítico",
                    $"{name} coincide con un patrón crítico.",
                    "Guarde la definición actual (respaldo) y pruebe con los escenarios del panel «Datos de entrada» antes de desplegar.", name, false));
            }
        }

        public override void Visit(CreateProcedureStatement node) => _isProcedure = true;

        public override void Visit(PredicateSetStatement node)
        {
            if (node.Options.HasFlag(SetOptions.NoCount) && node.IsOn) _noCount = true;
        }

        public override void Visit(BeginTransactionStatement node) => _beginTran = true;
        public override void Visit(TryCatchStatement node) => _tryCatch = true;

        public override void Visit(TableHint node)
        {
            switch (node.HintKind)
            {
                case TableHintKind.NoLock:
                    Add(new RiskFinding("nolock", "medium", "Hint NOLOCK",
                        "Permite lecturas sucias: puede leer datos sin confirmar, duplicados o faltantes.",
                        "En SQL Server, considere READ_COMMITTED_SNAPSHOT en lugar de NOLOCK.", null, false));
                    break;
                case TableHintKind.TabLock:
                case TableHintKind.TabLockX:
                case TableHintKind.XLock:
                case TableHintKind.HoldLock:
                    Add(new RiskFinding("lock-pesado", "medium", $"Lock pesado ({node.HintKind})",
                        "Bloquea la tabla o mantiene bloqueos hasta el final de la transacción: otras sesiones esperarán.",
                        "Use el nivel de bloqueo mínimo necesario y transacciones cortas.", null, false));
                    break;
            }
        }

        public override void Visit(DeclareCursorStatement node)
        {
            Add(new RiskFinding("cursor", "medium", "Cursor",
                "Procesa fila a fila: suele ser mucho más lento que una operación por conjuntos.",
                "Reescriba como UPDATE/INSERT ... SELECT por conjuntos si es posible.", null, true));
        }

        public override void Visit(SelectStarExpression node)
        {
            Add(new RiskFinding("select-star", "low", "SELECT *",
                "Trae todas las columnas: más lectura, más red y se rompe si cambia la tabla.",
                "Liste solo las columnas necesarias.", null, true));
        }

        public override void Visit(ExecuteStatement node)
        {
            if (node.ExecuteSpecification.ExecutableEntity is ExecutableStringList)
            {
                Add(new RiskFinding("sql-dinamico", "medium", "SQL dinámico (EXEC de texto)",
                    "El texto se arma en tiempo de ejecución: riesgo de inyección y dependencias invisibles para el grafo.",
                    "Use sp_executesql con parámetros o reescriba sin SQL dinámico.", null, false));
            }
            else if (node.ExecuteSpecification.ExecutableEntity is ExecutableProcedureReference reference
                     && reference.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier.Value
                         .Equals("sp_executesql", StringComparison.OrdinalIgnoreCase) == true)
            {
                Add(new RiskFinding("sql-dinamico", "medium", "SQL dinámico (sp_executesql)",
                    "Las dependencias reales pueden no verse en el grafo.",
                    "Verifique que los valores vayan como parámetros y no concatenados.", null, false));
            }
        }
    }
}
