using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlGraphCopilot.Application.Flow;

namespace SqlGraphCopilot.Infrastructure.Flow;

public static class ProcedureFlowAnalyzer
{
    public static ProcedureFlowReport Analyze(string schema, string name, string definition)
    {
        List<string> warnings = new();
        TSql160Parser parser = new(initialQuotedIdentifiers: true);
        using StringReader reader = new(definition);
        TSqlFragment? fragment = parser.Parse(reader, out IList<ParseError> errors);
        if (errors.Count > 0)
        {
            warnings.Add($"El código tiene {errors.Count} error(es) de sintaxis para el analizador; el resultado puede estar incompleto. Primero: línea {errors[0].Line}: {errors[0].Message}");
        }

        ProcedureStatementBodyBase? body = (fragment as TSqlScript)?.Batches
            .SelectMany(b => b.Statements)
            .OfType<ProcedureStatementBodyBase>()
            .FirstOrDefault();

        if (body is null)
        {
            warnings.Add("El objeto no es un procedimiento almacenado que el analizador pueda leer.");
            return new ProcedureFlowReport(schema, name, [], [], [], warnings, []);
        }

        List<FlowParameter> parameters = body.Parameters.Select(p => new FlowParameter(
            Name: p.VariableName.Value,
            DataType: Text(definition, p.DataType),
            IsOptional: p.Value is not null,
            DefaultValue: p.Value is null ? null : Text(definition, p.Value),
            IsOutput: p.Modifier == ParameterModifier.Output)).ToList();

        Walker walker = new(definition, parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        walker.WalkList(body.StatementList?.Statements ?? [], [], 0, 0);

        if (walker.Branches.Any(b => b.Kind == "dynamic"))
        {
            warnings.Add("Hay SQL dinámico (EXEC de texto o sp_executesql): sus destinos y condiciones no se pueden deducir.");
        }

        return new ProcedureFlowReport(schema, name, parameters, walker.Branches, walker.Guards, warnings, walker.Steps);
    }

    private sealed class Walker
    {
        private readonly string _source;
        private readonly HashSet<string> _parameters;
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        private int _nextId = 1;

        public Walker(string source, HashSet<string> parameters)
        {
            _source = source;
            _parameters = parameters;
        }

        public List<FlowBranch> Branches { get; } = new();
        public List<FlowGuard> Guards { get; } = new();
        public List<FlowStep> Steps { get; } = new();

        public void WalkList(IList<TSqlStatement> statements, List<FlowCondition> context, int parent, int depth)
        {
            List<FlowCondition> current = new(context);
            foreach (TSqlStatement statement in statements)
            {
                Walk(statement, current, parent, depth);

                if (statement is IfStatement ifs)
                {
                    string? thenEnd = Terminator(ifs.ThenStatement);
                    string? elseEnd = ifs.ElseStatement is null ? null : Terminator(ifs.ElseStatement);
                    if (thenEnd is not null)
                    {
                        Guards.Add(new FlowGuard(Condition(ifs.Predicate, false), thenEnd));
                    }
                    if (elseEnd is not null)
                    {
                        Guards.Add(new FlowGuard(Condition(ifs.Predicate, true), elseEnd));
                    }
                    if (thenEnd is not null && elseEnd is null) current.Add(Condition(ifs.Predicate, true));
                    else if (elseEnd is not null && thenEnd is null) current.Add(Condition(ifs.Predicate, false));
                    else if (thenEnd is not null && elseEnd is not null) break;
                }
                else if (statement is ReturnStatement or ThrowStatement)
                {
                    break;
                }
            }
        }

        private void Walk(TSqlStatement statement, List<FlowCondition> context, int parent, int depth)
        {
            switch (statement)
            {
                case IfStatement ifs:
                    {
                        FlowCondition yes = Condition(ifs.Predicate, false);
                        FlowCondition no = Condition(ifs.Predicate, true);
                        bool guard = Terminator(ifs.ThenStatement) is not null
                            || (ifs.ElseStatement is not null && Terminator(ifs.ElseStatement) is not null);
                        int decision = AddStep("decision", ifs, parent, depth, $"Si {Compact(Text(_source, ifs.Predicate))}",
                            guard ? "validación" : null, [], context, [], null, false);

                        List<FlowCondition> whenYes = [.. context, yes];
                        int thenId = AddStep("then", ifs.ThenStatement, decision, depth + 1, "Si se cumple", null, [], whenYes, [], null, true);
                        Walk(ifs.ThenStatement, whenYes, thenId, depth + 2);

                        if (ifs.ElseStatement is not null)
                        {
                            List<FlowCondition> whenNo = [.. context, no];
                            int elseId = AddStep("else", ifs.ElseStatement, decision, depth + 1, "Si no se cumple", null, [], whenNo, [], null, true);
                            Walk(ifs.ElseStatement, whenNo, elseId, depth + 2);
                        }
                        break;
                    }

                case BeginEndBlockStatement block:
                    WalkList(block.StatementList.Statements, context, parent, depth);
                    break;

                case WhileStatement loop:
                    {
                        FlowCondition repeat = Condition(loop.Predicate, false);
                        List<FlowCondition> inside = [.. context, repeat];
                        int loopId = AddStep("loop", loop, parent, depth, $"Repite mientras {Compact(Text(_source, loop.Predicate))}",
                            null, [], inside, [], loop.Statement, true);
                        Walk(loop.Statement, inside, loopId, depth + 1);
                        break;
                    }

                case TryCatchStatement tryCatch:
                    {
                        int tryId = AddStep("try", tryCatch, parent, depth, "Intenta ejecutar (TRY)", null, [], context, [], null, false);
                        WalkList(tryCatch.TryStatements.Statements, context, tryId, depth + 1);

                        List<FlowCondition> failed =
                            [.. context, new FlowCondition("si ocurre un error en el bloque TRY", [], false, [], true)];
                        int catchId = AddStep("catch", tryCatch.CatchStatements, parent, depth, "Si ocurre un error (CATCH)", null, [], failed, [],
                            tryCatch.CatchStatements, tryCatch.CatchStatements.Statements.Count > 0);
                        WalkList(tryCatch.CatchStatements.Statements, failed, catchId, depth + 1);
                        break;
                    }

                case ExecuteStatement exec:
                    AddExec(exec, context, parent, depth);
                    break;

                case InsertStatement insert:
                    AddWrite("INSERT", insert.InsertSpecification.Target, null, insert, context, parent, depth);
                    break;
                case UpdateStatement update:
                    AddWrite("UPDATE", update.UpdateSpecification.Target, update.UpdateSpecification.FromClause, update, context, parent, depth);
                    break;
                case DeleteStatement delete:
                    AddWrite("DELETE", delete.DeleteSpecification.Target, delete.DeleteSpecification.FromClause, delete, context, parent, depth);
                    break;
                case MergeStatement merge:
                    AddWrite("MERGE", merge.MergeSpecification.Target, null, merge, context, parent, depth);
                    break;
                case TruncateTableStatement truncate:
                    {
                        string table = Name(truncate.TableName);
                        Add("write", table, [], context);
                        AddStep("write", truncate, parent, depth, $"Vacía la tabla {FullName(truncate.TableName)} (TRUNCATE)", "TRUNCATE",
                            [FullName(truncate.TableName)], context, [], null, true);
                        break;
                    }

                case SelectStatement select:
                    AddSelect(select, context, parent, depth);
                    break;

                case ReturnStatement ret:
                    AddStep("return", ret, parent, depth,
                        ret.Expression is null ? "Termina el procedimiento (RETURN)" : $"Termina el procedimiento devolviendo {Compact(Text(_source, ret.Expression))}",
                        null, [], context, [], null, true);
                    break;

                case ThrowStatement thr:
                    AddStep("throw", thr, parent, depth,
                        thr.Message is null ? "Relanza el error (THROW)" : $"Lanza un error: {Compact(Text(_source, thr.Message))}",
                        null, [], context, [], null, true);
                    break;

                case RaiseErrorStatement raise:
                    AddStep("throw", raise, parent, depth, $"Emite un mensaje de error (RAISERROR): {Compact(Text(_source, raise.FirstParameter))}",
                        null, [], context, [], null, true);
                    break;

                case BeginTransactionStatement:
                    AddStep("transaction", statement, parent, depth, "Inicia una transacción", "BEGIN", [], context, [], null, true);
                    break;
                case CommitTransactionStatement:
                    AddStep("transaction", statement, parent, depth, "Confirma la transacción (COMMIT)", "COMMIT", [], context, [], null, true);
                    break;
                case RollbackTransactionStatement:
                    AddStep("transaction", statement, parent, depth, "Deshace la transacción (ROLLBACK)", "ROLLBACK", [], context, [], null, true);
                    break;
            }
        }

        private void AddExec(ExecuteStatement exec, List<FlowCondition> context, int parent, int depth)
        {
            if (exec.ExecuteSpecification.ExecutableEntity is not ExecutableProcedureReference reference)
            {
                Add("dynamic", "EXEC (texto dinámico)", [], context);
                AddStep("dynamic", exec, parent, depth, "Ejecuta SQL dinámico (texto): destino no deducible", null, [], context, [], null, true);
                return;
            }

            SchemaObjectName? procedure = reference.ProcedureReference?.ProcedureReference?.Name;
            if (procedure is null)
            {
                Add("dynamic", "EXEC @variable", [], context);
                AddStep("dynamic", exec, parent, depth, "Ejecuta un procedimiento elegido por variable: destino no deducible", null, [], context, [], null, true);
                return;
            }

            string target = Name(procedure);
            List<FlowArgument> arguments = reference.Parameters
                .Select((p, index) => new FlowArgument(p.Variable?.Name ?? $"#{index + 1}", Compact(Text(_source, p.ParameterValue))))
                .ToList();

            bool dynamic = procedure.BaseIdentifier.Value.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase);
            Add(dynamic ? "dynamic" : "exec", dynamic ? "sp_executesql (SQL dinámico)" : target, arguments, context);

            if (dynamic)
            {
                AddStep("dynamic", exec, parent, depth, "Ejecuta SQL dinámico (sp_executesql): destino no deducible", null, [], context, arguments, null, true);
            }
            else
            {
                AddStep("exec", exec, parent, depth, $"Llama al procedimiento {FullName(procedure)}", null, [FullName(procedure)], context, arguments, null, true);
            }
        }

        private void AddWrite(string operation, TableReference? table, FromClause? from, TSqlStatement statement,
            List<FlowCondition> context, int parent, int depth)
        {
            string full;
            string? local = null;
            if (table is NamedTableReference named)
            {
                SchemaObjectName resolved = ResolveAlias(named.SchemaObject, from);
                full = FullName(resolved);
                local = Name(resolved);
            }
            else if (table is VariableTableReference variable)
            {
                full = variable.Variable.Name;
            }
            else
            {
                return;
            }

            if (local is not null && !local.Contains('#') && !local.StartsWith("@"))
            {
                Add("write", local, [], context);
            }

            string verb = operation switch
            {
                "INSERT" => "Inserta registros en",
                "UPDATE" => "Actualiza",
                "DELETE" => "Elimina registros de",
                _ => "Combina datos (MERGE) en"
            };
            string kindOfTable = full.StartsWith('#') ? " (tabla temporal)" : full.StartsWith('@') ? " (variable de tabla)" : string.Empty;
            AddStep("write", statement, parent, depth, $"{verb} {full}{kindOfTable}", operation, [full], context, [], null, true);
        }

        private void AddSelect(SelectStatement select, List<FlowCondition> context, int parent, int depth)
        {
            HashSet<string> ctes = new(StringComparer.OrdinalIgnoreCase);
            if (select.WithCtesAndXmlNamespaces is not null)
            {
                foreach (CommonTableExpression cte in select.WithCtesAndXmlNamespaces.CommonTableExpressions)
                {
                    ctes.Add(cte.ExpressionName.Value);
                }
            }

            TableCollector collector = new();
            select.Accept(collector);
            List<string> tables = collector.Tables
                .Where(t => t.SchemaObject.SchemaIdentifier is not null || !ctes.Contains(t.SchemaObject.BaseIdentifier.Value))
                .Select(t => FullName(t.SchemaObject))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (select.Into is not null)
            {
                string into = FullName(select.Into);
                string from = tables.Count == 0 ? string.Empty : $" con datos de {string.Join(", ", tables)}";
                string local = Name(select.Into);
                if (!local.Contains('#')) Add("write", local, [], context);
                AddStep("write", select, parent, depth, $"Crea y llena {into} (SELECT INTO){from}", "SELECT INTO", [into], context, [], null, true);
                return;
            }

            if (tables.Count == 0) return;
            AddStep("read", select, parent, depth, $"Lee {string.Join(", ", tables)}", null, tables, context, [], null, true);
        }

        private void Add(string kind, string target, List<FlowArgument> arguments, List<FlowCondition> context)
        {
            string key = $"{kind}|{target}|{string.Join("&", context.Select(c => c.Text))}";
            if (!_seen.Add(key)) return;
            Branches.Add(new FlowBranch(kind, target, [.. context], arguments));
        }

        private int AddStep(string kind, TSqlFragment node, int parent, int depth, string title, string? operation,
            IReadOnlyList<string> targets, List<FlowCondition> context, IReadOnlyList<FlowArgument> arguments,
            TSqlFragment? range, bool instrument)
        {
            int id = _nextId++;
            Steps.Add(new FlowStep(id, parent, depth, kind, node.StartLine, title, operation, targets, [.. context], arguments));
            return id;
        }

        private static SchemaObjectName ResolveAlias(SchemaObjectName name, FromClause? from)
        {
            if (from is null || name.SchemaIdentifier is not null) return name;
            AliasCollector aliases = new();
            from.Accept(aliases);
            return aliases.ByAlias.TryGetValue(name.BaseIdentifier.Value, out SchemaObjectName? real) ? real : name;
        }

        private static string FullName(SchemaObjectName name)
        {
            string baseName = name.BaseIdentifier.Value;
            if (baseName.StartsWith('#')) return baseName;
            List<string> parts = new();
            if (name.ServerIdentifier is not null) parts.Add(name.ServerIdentifier.Value);
            if (name.DatabaseIdentifier is not null) parts.Add(name.DatabaseIdentifier.Value);
            parts.Add(name.SchemaIdentifier?.Value ?? "dbo");
            parts.Add(baseName);
            return string.Join('.', parts);
        }

        private FlowCondition Condition(BooleanExpression expression, bool negate)
        {
            List<ParamConstraint> constraints = new();
            bool exact = Decompose(expression, negate, constraints);

            string text = negate && exact && constraints.Count == 1
                ? Render(constraints[0])
                : negate ? $"NOT ({Compact(Text(_source, expression))})" : Compact(Text(_source, expression));

            VariableCollector collector = new();
            expression.Accept(collector);
            List<string> usedParameters = collector.Variables.Where(_parameters.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            bool localVariables = collector.Variables.Any(v => !_parameters.Contains(v));

            return new FlowCondition(text, constraints, exact, usedParameters, collector.HasQuery || collector.HasFunction || localVariables);
        }

        private bool Decompose(BooleanExpression expression, bool negate, List<ParamConstraint> output)
        {
            switch (expression)
            {
                case BooleanParenthesisExpression parenthesis:
                    return Decompose(parenthesis.Expression, negate, output);

                case BooleanNotExpression not:
                    return Decompose(not.Expression, !negate, output);

                case BooleanBinaryExpression binary:
                    {
                        bool and = binary.BinaryExpressionType == BooleanBinaryExpressionType.And;
                        if (and == !negate)
                        {
                            bool left = Decompose(binary.FirstExpression, negate, output);
                            bool right = Decompose(binary.SecondExpression, negate, output);
                            return left && right;
                        }
                        return false;
                    }

                case BooleanComparisonExpression comparison:
                    {
                        (ScalarExpression? variable, ScalarExpression? literal, bool flipped) = Split(comparison.FirstExpression, comparison.SecondExpression);
                        if (variable is not VariableReference reference || !_parameters.Contains(reference.Name)) return false;
                        string? value = Literal(literal);
                        string? op = Operator(comparison.ComparisonType, flipped);
                        if (value is null || op is null) return false;
                        output.Add(new ParamConstraint(reference.Name, negate ? Negate(op) : op, [value]));
                        return true;
                    }

                case BooleanIsNullExpression isNull when isNull.Expression is VariableReference reference && _parameters.Contains(reference.Name):
                    output.Add(new ParamConstraint(reference.Name, isNull.IsNot != negate ? "IS NOT NULL" : "IS NULL", []));
                    return true;

                case InPredicate inPredicate when inPredicate.Subquery is null
                    && inPredicate.Expression is VariableReference reference
                    && _parameters.Contains(reference.Name):
                    {
                        List<string> values = inPredicate.Values.Select(Literal).Where(v => v is not null).Select(v => v!).ToList();
                        if (values.Count != inPredicate.Values.Count) return false;
                        output.Add(new ParamConstraint(reference.Name, inPredicate.NotDefined != negate ? "NOT IN" : "IN", values));
                        return true;
                    }

                default:
                    return false;
            }
        }

        private static (ScalarExpression? Variable, ScalarExpression? Literal, bool Flipped) Split(ScalarExpression first, ScalarExpression second) =>
            first is VariableReference ? (first, second, false) : second is VariableReference ? (second, first, true) : (null, null, false);

        private static string? Literal(ScalarExpression? expression) => expression switch
        {
            StringLiteral s => s.Value,
            IntegerLiteral i => i.Value,
            NumericLiteral n => n.Value,
            RealLiteral r => r.Value,
            UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: IntegerLiteral i } => "-" + i.Value,
            UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: NumericLiteral n } => "-" + n.Value,
            ParenthesisExpression p => Literal(p.Expression),
            _ => null
        };

        private static string? Operator(BooleanComparisonType type, bool flipped) => type switch
        {
            BooleanComparisonType.Equals => "=",
            BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => "<>",
            BooleanComparisonType.GreaterThan => flipped ? "<" : ">",
            BooleanComparisonType.GreaterThanOrEqualTo => flipped ? "<=" : ">=",
            BooleanComparisonType.LessThan => flipped ? ">" : "<",
            BooleanComparisonType.LessThanOrEqualTo => flipped ? ">=" : "<=",
            _ => null
        };

        private static string Negate(string op) => op switch
        {
            "=" => "<>",
            "<>" => "=",
            ">" => "<=",
            "<=" => ">",
            "<" => ">=",
            ">=" => "<",
            "IS NULL" => "IS NOT NULL",
            "IS NOT NULL" => "IS NULL",
            "IN" => "NOT IN",
            "NOT IN" => "IN",
            _ => op
        };

        private static string Render(ParamConstraint c) => c.Operator switch
        {
            "IS NULL" or "IS NOT NULL" => $"{c.Parameter} {c.Operator}",
            "IN" or "NOT IN" => $"{c.Parameter} {c.Operator} ({string.Join(", ", c.Values.Select(Quote))})",
            _ => $"{c.Parameter} {c.Operator} {Quote(c.Values[0])}"
        };

        private static string Quote(string value) => double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _) ? value : $"'{value}'";

        private static string? Terminator(TSqlStatement statement) => statement switch
        {
            ReturnStatement => "RETURN",
            ThrowStatement => "THROW",
            BeginEndBlockStatement block => block.StatementList.Statements.Select(Terminator).FirstOrDefault(t => t is not null),
            _ => null
        };

        private static string Name(SchemaObjectName name) =>
            $"{name.SchemaIdentifier?.Value ?? "dbo"}.{name.BaseIdentifier.Value}";
    }

    private sealed class TableCollector : TSqlFragmentVisitor
    {
        public List<NamedTableReference> Tables { get; } = new();

        public override void Visit(NamedTableReference node) => Tables.Add(node);
    }

    private sealed class AliasCollector : TSqlFragmentVisitor
    {
        public Dictionary<string, SchemaObjectName> ByAlias { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(NamedTableReference node)
        {
            if (node.Alias is not null) ByAlias[node.Alias.Value] = node.SchemaObject;
        }
    }

    private sealed class VariableCollector : TSqlFragmentVisitor
    {
        public List<string> Variables { get; } = new();
        public bool HasQuery { get; private set; }
        public bool HasFunction { get; private set; }

        public override void Visit(VariableReference node) => Variables.Add(node.Name);
        public override void Visit(ScalarSubquery node) => HasQuery = true;
        public override void Visit(ExistsPredicate node) => HasQuery = true;
        public override void Visit(FunctionCall node) => HasFunction = true;
    }

    private static string Text(string source, TSqlFragment fragment) =>
        fragment.StartOffset >= 0 && fragment.StartOffset + fragment.FragmentLength <= source.Length
            ? source.Substring(fragment.StartOffset, fragment.FragmentLength)
            : string.Empty;

    private static string Compact(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
