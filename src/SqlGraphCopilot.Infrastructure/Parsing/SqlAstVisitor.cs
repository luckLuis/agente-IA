using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlGraphCopilot.Application.Parsing;

namespace SqlGraphCopilot.Infrastructure.Parsing;

/// <summary>
/// Recorre el AST de ScriptDom y clasifica cada referencia en lectura, escritura, ejecución o llamada.
///
/// Correcciones respecto a la versión anterior:
///  1. El destino de INSERT/UPDATE/DELETE/MERGE ya no se vuelve a marcar como lectura
///     (antes se quitaba de ReadTables y el recorrido de los hijos lo volvía a agregar).
///  2. UPDATE alias SET ... FROM dbo.Tabla alias: se resuelve el alias a la tabla real (antes quedaba "dbo.alias").
///  3. Los nombres de CTE, tablas temporales (#) y variables de tabla (@) ya no aparecen como tablas "dbo.X".
///  4. Detecta SQL dinámico (sp_executesql, EXEC(@sql), EXEC @proc) y OPENQUERY / EXEC ... AT.
///  5. Conserva nombres de 3 y 4 partes (cross-database y linked server).
///  6. SELECT INTO, TRUNCATE y OUTPUT INTO cuentan como escritura; funciones escalares y TVF como llamadas.
/// </summary>
public sealed class SqlAstVisitor : TSqlFragmentVisitor
{
    private readonly List<SqlObjectName> _reads = new();
    private readonly List<SqlObjectName> _writeTargets = new();
    private readonly List<SqlObjectName> _executes = new();
    private readonly List<SqlObjectName> _calls = new();
    private readonly HashSet<string> _linkedServers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _cteNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SqlObjectName> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<TSqlFragment> _targetFragments = new(ReferenceEqualityComparer.Instance);

    public string? ObjectName { get; private set; }
    public bool HasDynamicSql { get; private set; }
    public bool UsesOpenQuery { get; private set; }

    // ---- Nombre del módulo -------------------------------------------------------------

    public override void Visit(ProcedureStatementBody node)
    {
        ObjectName ??= ToName(node.ProcedureReference?.Name)?.ToString();
    }

    public override void Visit(FunctionStatementBody node)
    {
        ObjectName ??= ToName(node.Name)?.ToString();
    }

    public override void Visit(ViewStatementBody node)
    {
        ObjectName ??= ToName(node.SchemaObjectName)?.ToString();
    }

    public override void Visit(TriggerStatementBody node)
    {
        ObjectName ??= ToName(node.Name)?.ToString();
    }

    // ---- Lecturas ----------------------------------------------------------------------

    public override void Visit(CommonTableExpression node)
    {
        if (node.ExpressionName?.Value is { } cte)
        {
            _cteNames.Add(cte);
        }
    }

    public override void Visit(NamedTableReference node)
    {
        SqlObjectName? name = ToName(node.SchemaObject);
        if (name is null)
        {
            return;
        }

        if (node.Alias?.Value is { } alias)
        {
            _aliases[alias] = name;
        }

        if (name.Server is not null)
        {
            _linkedServers.Add(name.Server);
        }

        // El destino de una escritura se registra en _writeTargets, no como lectura.
        if (!_targetFragments.Contains(node))
        {
            _reads.Add(name);
        }
    }

    public override void Visit(SchemaObjectFunctionTableReference node)
    {
        if (ToName(node.SchemaObject) is { } name)
        {
            _calls.Add(name);
            if (node.Alias?.Value is { } alias)
            {
                _aliases[alias] = name;
            }
        }
    }

    public override void Visit(FunctionCall node)
    {
        // Solo funciones definidas por el usuario (con esquema): dbo.fn_X(...). Las built-in no tienen CallTarget.
        if (node.CallTarget is MultiPartIdentifierCallTarget target &&
            target.MultiPartIdentifier?.Identifiers is { Count: > 0 } parts &&
            node.FunctionName?.Value is { } functionName)
        {
            string? schema = parts[^1].Value;
            string? database = parts.Count >= 2 ? parts[^2].Value : null;
            string? server = parts.Count >= 3 ? parts[^3].Value : null;
            _calls.Add(new SqlObjectName(server, database, schema, functionName));
        }
    }

    public override void Visit(OpenQueryTableReference node)
    {
        UsesOpenQuery = true;
        if (node.LinkedServer?.Value is { } server)
        {
            _linkedServers.Add(server);
        }
    }

    // ---- Escrituras --------------------------------------------------------------------

    public override void Visit(DataModificationSpecification node)
    {
        // Se ejecuta ANTES de visitar los hijos: así el NamedTableReference destino se reconoce como escritura.
        RegisterWriteTarget(node.Target);
    }

    public override void Visit(OutputIntoClause node)
    {
        RegisterWriteTarget(node.IntoTable);
    }

    public override void Visit(SelectStatement node)
    {
        if (ToName(node.Into) is { } into)
        {
            _writeTargets.Add(into);
        }
    }

    public override void Visit(TruncateTableStatement node)
    {
        if (ToName(node.TableName) is { } table)
        {
            _writeTargets.Add(table);
        }
    }

    // ---- Ejecuciones -------------------------------------------------------------------

    public override void Visit(ExecuteSpecification node)
    {
        if (node.LinkedServer?.Value is { } atServer)
        {
            // EXEC ('...') AT ServidorVinculado
            _linkedServers.Add(atServer);
            UsesOpenQuery = true;
        }

        switch (node.ExecutableEntity)
        {
            case ExecutableStringList:
                HasDynamicSql = true; // EXEC (@sql) / EXEC ('...')
                break;

            case ExecutableProcedureReference procedure:
                if (procedure.ProcedureReference?.ProcedureVariable is not null)
                {
                    HasDynamicSql = true; // EXEC @nombreProc
                    break;
                }

                SqlObjectName? name = ToName(procedure.ProcedureReference?.ProcedureReference?.Name);
                if (name is null)
                {
                    break;
                }

                if (name.Name.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase))
                {
                    HasDynamicSql = true;
                    break;
                }

                if (name.Server is not null)
                {
                    _linkedServers.Add(name.Server);
                }
                _executes.Add(name);
                break;
        }
    }

    // ---- Resultado ---------------------------------------------------------------------

    public SqlAnalysisResult BuildResult(IReadOnlyList<string> errors) => new(
        ObjectName,
        Normalize(_reads),
        Normalize(_writeTargets),
        Normalize(_executes),
        Normalize(_calls),
        _linkedServers.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
        HasDynamicSql,
        UsesOpenQuery,
        errors);

    private void RegisterWriteTarget(TableReference? target)
    {
        if (target is NamedTableReference named && ToName(named.SchemaObject) is { } name)
        {
            _targetFragments.Add(named);
            _writeTargets.Add(name);
        }
    }

    private IReadOnlyList<SqlObjectName> Normalize(IEnumerable<SqlObjectName> names)
    {
        Dictionary<string, SqlObjectName> unique = new(StringComparer.OrdinalIgnoreCase);
        foreach (SqlObjectName raw in names)
        {
            SqlObjectName? resolved = Resolve(raw);
            if (resolved is not null)
            {
                unique.TryAdd(resolved.ToString(), resolved);
            }
        }
        return unique.Values.ToList();
    }

    /// <summary>Descarta CTEs, temporales y variables; traduce alias al objeto real.</summary>
    private SqlObjectName? Resolve(SqlObjectName name)
    {
        if (IsTemporary(name.Name))
        {
            return null;
        }

        if (name.IsQualified)
        {
            return name;
        }

        if (_cteNames.Contains(name.Name))
        {
            return null;
        }

        if (_aliases.TryGetValue(name.Name, out SqlObjectName? aliased) &&
            !aliased.Name.Equals(name.Name, StringComparison.OrdinalIgnoreCase))
        {
            return IsTemporary(aliased.Name) || (!aliased.IsQualified && _cteNames.Contains(aliased.Name)) ? null : aliased;
        }

        return name;
    }

    private static bool IsTemporary(string name) => name.StartsWith('#') || name.StartsWith('@');

    private static SqlObjectName? ToName(SchemaObjectName? schemaObject)
    {
        string? baseName = schemaObject?.BaseIdentifier?.Value;
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return null;
        }

        return new SqlObjectName(
            schemaObject!.ServerIdentifier?.Value,
            schemaObject.DatabaseIdentifier?.Value,
            schemaObject.SchemaIdentifier?.Value,
            baseName);
    }
}
