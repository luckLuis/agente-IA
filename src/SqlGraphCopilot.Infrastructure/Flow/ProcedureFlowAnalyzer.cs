using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlGraphCopilot.Application.Flow;

namespace SqlGraphCopilot.Infrastructure.Flow;

/// <summary>
/// Lee el código de un procedimiento y obtiene sus parámetros y, para cada EXEC / escritura,
/// las condiciones (IF / ELSE / WHILE / validaciones con RETURN) que deben cumplirse para llegar a ella.
/// Es un análisis estático: no ejecuta nada.
/// </summary>
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
            return new ProcedureFlowReport(schema, name, [], [], [], warnings);
        }

        List<FlowParameter> parameters = body.Parameters.Select(p => new FlowParameter(
            Name: p.VariableName.Value,
            DataType: Text(definition, p.DataType),
            IsOptional: p.Value is not null,
            DefaultValue: p.Value is null ? null : Text(definition, p.Value),
            IsOutput: p.Modifier == ParameterModifier.Output)).ToList();

        Walker walker = new(definition, parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        walker.WalkList(body.StatementList?.Statements ?? [], []);

        if (walker.Branches.Any(b => b.Kind == "dynamic"))
        {
            warnings.Add("Hay SQL dinámico (EXEC de texto o sp_executesql): sus destinos y condiciones no se pueden deducir.");
        }

        return new ProcedureFlowReport(schema, name, parameters, walker.Branches, walker.Guards, warnings);
    }

    // ---- Recorrido -------------------------------------------------------------------------

    private sealed class Walker
    {
        private readonly string _source;
        private readonly HashSet<string> _parameters;
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public Walker(string source, HashSet<string> parameters)
        {
            _source = source;
            _parameters = parameters;
        }

        public List<FlowBranch> Branches { get; } = new();
        public List<FlowGuard> Guards { get; } = new();

        public void WalkList(IList<TSqlStatement> statements, List<FlowCondition> context)
        {
            // Un IF que termina con RETURN/THROW condiciona todo lo que viene después en el mismo bloque.
            List<FlowCondition> current = new(context);
            foreach (TSqlStatement statement in statements)
            {
                Walk(statement, current);

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
                    break;   // lo que sigue es código inalcanzable
                }
            }
        }

        private void Walk(TSqlStatement statement, List<FlowCondition> context)
        {
            switch (statement)
            {
                case IfStatement ifs:
                    Walk(ifs.ThenStatement, [.. context, Condition(ifs.Predicate, false)]);
                    if (ifs.ElseStatement is not null)
                    {
                        Walk(ifs.ElseStatement, [.. context, Condition(ifs.Predicate, true)]);
                    }
                    break;

                case BeginEndBlockStatement block:
                    WalkList(block.StatementList.Statements, context);
                    break;

                case WhileStatement loop:
                    Walk(loop.Statement, [.. context, Condition(loop.Predicate, false)]);
                    break;

                case TryCatchStatement tryCatch:
                    WalkList(tryCatch.TryStatements.Statements, context);
                    WalkList(tryCatch.CatchStatements.Statements,
                        [.. context, new FlowCondition("si ocurre un error en el bloque TRY", [], false, [], true)]);
                    break;

                case ExecuteStatement exec:
                    AddExec(exec, context);
                    break;

                case InsertStatement insert:
                    AddWrite(insert.InsertSpecification.Target, context);
                    break;
                case UpdateStatement update:
                    AddWrite(update.UpdateSpecification.Target, context);
                    break;
                case DeleteStatement delete:
                    AddWrite(delete.DeleteSpecification.Target, context);
                    break;
                case MergeStatement merge:
                    AddWrite(merge.MergeSpecification.Target, context);
                    break;
                case TruncateTableStatement truncate:
                    Add("write", Name(truncate.TableName), [], context);
                    break;
            }
        }

        private void AddExec(ExecuteStatement exec, List<FlowCondition> context)
        {
            if (exec.ExecuteSpecification.ExecutableEntity is not ExecutableProcedureReference reference)
            {
                Add("dynamic", "EXEC (texto dinámico)", [], context);   // EXEC('...')
                return;
            }

            SchemaObjectName? procedure = reference.ProcedureReference?.ProcedureReference?.Name;
            if (procedure is null)
            {
                Add("dynamic", "EXEC @variable", [], context);          // EXEC @proc
                return;
            }

            string target = Name(procedure);
            List<FlowArgument> arguments = reference.Parameters
                .Select((p, index) => new FlowArgument(p.Variable?.Name ?? $"#{index + 1}", Compact(Text(_source, p.ParameterValue))))
                .ToList();

            bool dynamic = procedure.BaseIdentifier.Value.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase);
            Add(dynamic ? "dynamic" : "exec", dynamic ? "sp_executesql (SQL dinámico)" : target, arguments, context);
        }

        private void AddWrite(TableReference? table, List<FlowCondition> context)
        {
            if (table is not NamedTableReference named) return;
            string target = Name(named.SchemaObject);
            if (target.Contains('#') || target.StartsWith("@")) return;   // temporales y variables de tabla
            Add("write", target, [], context);
        }

        private void Add(string kind, string target, List<FlowArgument> arguments, List<FlowCondition> context)
        {
            string key = $"{kind}|{target}|{string.Join("&", context.Select(c => c.Text))}";
            if (!_seen.Add(key)) return;
            Branches.Add(new FlowBranch(kind, target, [.. context], arguments));
        }

        // ---- Condiciones -------------------------------------------------------------------

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

        /// <summary>
        /// Extrae restricciones evaluables. Devuelve true si la expresión completa quedó representada
        /// (conjunción de comparaciones simples entre un parámetro y un literal, IS [NOT] NULL o IN con literales).
        /// </summary>
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
                        // A AND B  /  NOT (A OR B)  =>  se cumplen las dos partes. Lo demás no es una conjunción.
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

        // ---- Utilidades --------------------------------------------------------------------

        /// <summary>"RETURN" o "THROW" si la sentencia termina siempre la ejecución (a nivel del propio bloque); null si no.</summary>
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
