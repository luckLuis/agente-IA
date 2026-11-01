using System.Text;
using SqlGraphCopilot.Core.Analysis;
using SqlGraphCopilot.Core.Domain;
using SqlGraphCopilot.Core.Indexing;

namespace SqlGraphCopilot.Core.Agent;

/// <summary>
/// Redacta respuestas en Markdown a partir de los reportes, sin LLM.
/// Es el modo por defecto (no requiere aprobación de un proveedor de IA) y el respaldo si el LLM falla.
/// </summary>
public sealed class DeterministicNarrator
{
    private const int MaxListed = 8;

    public string Impact(ImpactReport report)
    {
        StringBuilder md = new();
        md.AppendLine($"**Impacto de `{report.TargetName}`** ({Label(report.TargetKind)}) — riesgo **{Label(report.Level)}** ({report.Score:0}/100)");
        md.AppendLine();

        if (report.TotalAffected == 0 && report.DataImpact.Count == 0)
        {
            md.AppendLine("- Ningún objeto SQL depende de él en las bases cargadas.");
        }
        else
        {
            md.AppendLine($"- **{report.TotalAffected}** objeto(s) afectados en cascada, **{report.CriticalAffected}** crítico(s).");

            List<ImpactedObject> direct = report.Affected.Where(a => a.Depth == 1 && !a.IsOtherDatabase).ToList();
            if (direct.Count > 0)
            {
                md.AppendLine($"- Directos: {JoinWithRelation(direct)}");
            }

            List<ImpactedObject> deeper = report.Affected.Where(a => a.Depth > 1 && !a.IsOtherDatabase).ToList();
            if (deeper.Count > 0)
            {
                md.AppendLine($"- Indirectos (hasta nivel {deeper.Max(d => d.Depth)}): {JoinNames(deeper.Select(d => d.Name))}");
            }

            List<ImpactedObject> otherDb = report.Affected.Where(a => a.IsOtherDatabase).ToList();
            if (otherDb.Count > 0)
            {
                md.AppendLine($"- **En otras bases:** {JoinNames(otherDb.Select(d => d.Name))}");
            }

            foreach (DataImpactEntry data in report.DataImpact.Take(4))
            {
                md.AppendLine($"- Por datos: escribe `{data.TableName}`, que leen {JoinNames(data.ReaderNames)}");
            }
        }

        md.AppendLine();
        md.AppendLine("**Recomendación**");
        foreach (string recommendation in Recommendations(report))
        {
            md.AppendLine($"- {recommendation}");
        }

        AppendWarnings(md, report.Warnings);
        return md.ToString().TrimEnd();
    }

    public string Explain(GraphNode node, DatabaseIndex index, ImpactReport impact)
    {
        StringBuilder md = new();
        md.AppendLine($"**`{impact.TargetName}`** — {Label(node.Kind)} en `{node.Key.Database}`");
        md.AppendLine();

        List<string> flags = new();
        if (node.IsCritical) flags.Add("crítico");
        if (node.HasDynamicSql) flags.Add("usa SQL dinámico");
        if (node.UsesOpenQuery) flags.Add("usa OPENQUERY");
        if (node.IsExternal) flags.Add("externo (no expandido)");
        if (node.Kind is NodeKind.Missing) flags.Add("NO EXISTE (referencia rota)");
        if (flags.Count > 0)
        {
            md.AppendLine($"- Marcas: {string.Join(", ", flags)}");
        }

        IReadOnlyList<GraphEdge> outgoing = index.Graph.GetOutgoing(node.Id);
        IReadOnlyList<GraphEdge> incoming = index.Graph.GetIncoming(node.Id);

        if (outgoing.Count > 0)
        {
            IEnumerable<string> uses = outgoing
                .Select(e => (Edge: e, Target: index.Graph.GetNode(e.TargetId)))
                .Where(x => x.Target is not null)
                .OrderBy(x => x.Target!.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxListed)
                .Select(x => $"`{x.Target!.DisplayName}` ({Relation(x.Edge.Kinds)})");
            md.AppendLine($"- Usa {outgoing.Count}: {string.Join(", ", uses)}{More(outgoing.Count)}");
        }

        if (incoming.Count > 0)
        {
            IEnumerable<string> usedBy = incoming
                .Select(e => (Edge: e, Source: index.Graph.GetNode(e.SourceId)))
                .Where(x => x.Source is not null)
                .OrderBy(x => x.Source!.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxListed)
                .Select(x => $"`{x.Source!.DisplayName}` ({Relation(x.Edge.Kinds)})");
            md.AppendLine($"- Lo usan {incoming.Count}: {string.Join(", ", usedBy)}{More(incoming.Count)}");
        }

        if (outgoing.Count == 0 && incoming.Count == 0)
        {
            md.AppendLine("- No tiene dependencias registradas en el catálogo.");
        }

        md.AppendLine($"- Riesgo si se modifica: **{Label(impact.Level)}** ({impact.TotalAffected} afectados en cascada).");
        AppendWarnings(md, impact.Warnings.Take(2).ToList());
        return md.ToString().TrimEnd();
    }

    public string SinglePointsOfFailure(IReadOnlyList<SpofFinding> findings)
    {
        if (findings.Count == 0)
        {
            return "No se detectaron puntos únicos de fallo: ningún objeto concentra dependencias de forma relevante.";
        }

        StringBuilder md = new();
        md.AppendLine("**Puntos únicos de fallo** (si fallan o cambian, arrastran a muchos objetos)");
        md.AppendLine();
        int position = 1;
        foreach (SpofFinding finding in findings.Take(MaxListed))
        {
            md.AppendLine($"{position++}. `{finding.Name}` ({Label(finding.Kind)}) — {string.Join("; ", finding.Reasons)}");
        }
        md.AppendLine();
        md.AppendLine("**Recomendación**");
        md.AppendLine("- Proteger estos objetos con pruebas de regresión y revisión obligatoria antes de cualquier DDL.");
        md.AppendLine("- Para tablas escritas por muchos SPs, evaluar un único SP de escritura (o vista/API) que centralice la regla.");
        return md.ToString().TrimEnd();
    }

    public string Coupling(IReadOnlyList<CouplingFinding> findings)
    {
        if (findings.Count == 0)
        {
            return "No hay objetos con dependencias en esta base.";
        }

        StringBuilder md = new();
        md.AppendLine("**Objetos más acoplados**");
        md.AppendLine();
        md.AppendLine("| Objeto | Tipo | Lo usan | Usa | Inestabilidad |");
        md.AppendLine("|---|---|---:|---:|---:|");
        foreach (CouplingFinding f in findings.Take(10))
        {
            md.AppendLine($"| `{f.Name}` | {Label(f.Kind)} | {f.FanIn} | {f.FanOut} | {f.Instability:0.00} |");
        }
        md.AppendLine();
        md.AppendLine("_Inestabilidad cercana a 0: muchos dependen de él (cambiar con cuidado). Cercana a 1: depende de mucho (frágil ante cambios ajenos)._");
        return md.ToString().TrimEnd();
    }

    public string Redundancy(RedundancyReport report)
    {
        if (report.ExactDuplicates.Count == 0 && report.SimilarProcedures.Count == 0)
        {
            return "No se encontraron objetos duplicados ni SPs con dependencias casi idénticas.";
        }

        StringBuilder md = new();
        if (report.ExactDuplicates.Count > 0)
        {
            md.AppendLine("**Definiciones idénticas**");
            foreach (DuplicateGroup group in report.ExactDuplicates.Take(MaxListed))
            {
                md.AppendLine($"- {JoinNames(group.Names)}");
            }
            md.AppendLine();
        }

        if (report.SimilarProcedures.Count > 0)
        {
            md.AppendLine("**SPs con dependencias casi iguales** (posible lógica duplicada)");
            foreach (SimilarPair pair in report.SimilarProcedures.Take(MaxListed))
            {
                md.AppendLine($"- `{pair.FirstName}` ↔ `{pair.SecondName}`: {pair.Similarity:P0} de similitud ({pair.SharedDependencies} objetos en común)");
            }
            md.AppendLine();
        }

        md.AppendLine("**Recomendación**");
        md.AppendLine("- Confirmar con el código si la lógica es la misma y consolidar en un solo SP parametrizado.");
        return md.ToString().TrimEnd();
    }

    public string Bottlenecks(BottleneckReport report)
    {
        StringBuilder md = new();
        md.AppendLine("**Posibles cuellos de botella**");
        md.AppendLine();
        bool any = false;

        if (report.WriteHotspots.Count > 0)
        {
            any = true;
            md.AppendLine("Tablas escritas por varios SPs (riesgo de bloqueos):");
            foreach (WriteHotspot hotspot in report.WriteHotspots.Take(5))
            {
                md.AppendLine($"- `{hotspot.TableName}`: {hotspot.Writers} escritores, {hotspot.Readers} lectores ({JoinNames(hotspot.WriterNames.Take(4))})");
            }
        }

        if (report.DeepCallChains.Count > 0)
        {
            any = true;
            md.AppendLine("Cadenas de EXEC muy profundas (transacciones largas):");
            foreach (CallChain chain in report.DeepCallChains.Take(3))
            {
                md.AppendLine($"- {chain.Depth} niveles: {string.Join(" → ", chain.Path.Select(p => $"`{p}`"))}");
            }
        }

        if (report.Cycles.Count > 0)
        {
            any = true;
            md.AppendLine("Ciclos de ejecución (A llama a B que llama a A):");
            foreach (CycleFinding cycle in report.Cycles.Take(3))
            {
                md.AppendLine($"- {JoinNames(cycle.Names)}");
            }
        }

        if (report.GodProcedures.Count > 0)
        {
            any = true;
            md.AppendLine("SPs que tocan demasiados objetos:");
            foreach (ObjectMetric god in report.GodProcedures.Take(5))
            {
                md.AppendLine($"- `{god.Name}`: {god.Value} objetos");
            }
        }

        if (report.DynamicSqlObjects.Count > 0)
        {
            any = true;
            md.AppendLine($"SQL dinámico (sin plan estable y opaco para este análisis): {JoinNames(report.DynamicSqlObjects.Select(d => d.Name))}");
        }

        if (!any)
        {
            md.AppendLine("No se detectaron patrones de riesgo con la información disponible.");
        }

        if (report.ClassificationCoverage < 0.999)
        {
            md.AppendLine();
            md.AppendLine($"> Cobertura de clasificación: {report.ClassificationCoverage:P0}. Ejecute el **escaneo profundo** para detectar todas las tablas con escrituras concurrentes.");
        }

        md.AppendLine();
        md.AppendLine("_Este análisis es estructural. Para tiempos reales, contrastar con Query Store o sys.dm_exec_procedure_stats._");
        return md.ToString().TrimEnd();
    }

    public string BrokenReferences(IReadOnlyList<BrokenReference> broken)
    {
        if (broken.Count == 0)
        {
            return "No hay referencias a objetos inexistentes en esta base.";
        }

        StringBuilder md = new();
        md.AppendLine($"**{broken.Count} referencia(s) rota(s)** — objetos que se usan pero no existen:");
        md.AppendLine();
        foreach (BrokenReference item in broken.Take(10))
        {
            md.AppendLine($"- `{item.MissingName}` usado por {JoinNames(item.ReferencedByNames)}");
        }
        md.AppendLine();
        md.AppendLine("_Algunas pueden ser tablas creadas en tiempo de ejecución (resolución diferida). El resto falla al ejecutarse._");
        return md.ToString().TrimEnd();
    }

    public string Overview(OverviewReport report)
    {
        DatabaseSummary s = report.Summary;
        StringBuilder md = new();
        md.AppendLine($"**Diagnóstico de `{s.Database}`** en `{s.Instance}`");
        md.AppendLine();
        string counts = string.Join(", ", s.ObjectCounts.OrderByDescending(c => c.Value).Select(c => $"{c.Value} {Label(c.Key)}(s)"));
        md.AppendLine($"- Objetos: {counts}. Relaciones: {s.EdgeCount}.");
        md.AppendLine($"- Referencias externas: {s.ExternalReferenceCount}; servidores vinculados: {s.LinkedServerCount}; rotas: {s.BrokenReferenceCount}.");
        md.AppendLine($"- Clasificación lee/escribe: {s.ClassificationCoverage:P0}; SQL dinámico: {s.DynamicSqlCount}.");

        if (report.SinglePointsOfFailure.Count > 0)
        {
            md.AppendLine($"- Puntos únicos de fallo: {JoinNames(report.SinglePointsOfFailure.Take(5).Select(f => f.Name))}");
        }
        if (report.Bottlenecks.WriteHotspots.Count > 0)
        {
            md.AppendLine($"- Tablas con escritura concurrente: {JoinNames(report.Bottlenecks.WriteHotspots.Take(5).Select(h => h.TableName))}");
        }
        int redundancies = report.Redundancy.ExactDuplicates.Count + report.Redundancy.SimilarProcedures.Count;
        if (redundancies > 0)
        {
            md.AppendLine($"- Posibles redundancias: {redundancies} grupo(s)/par(es).");
        }
        if (report.Bottlenecks.Cycles.Count > 0)
        {
            md.AppendLine($"- Ciclos de ejecución: {report.Bottlenecks.Cycles.Count}.");
        }

        AppendWarnings(md, report.Warnings);
        md.AppendLine();
        md.AppendLine("Pregunte por un objeto concreto (\"¿qué pasa si modifico `dbo.X`?\") o por un tema: puntos únicos de fallo, redundancias, cuellos de botella.");
        return md.ToString().TrimEnd();
    }

    public string Help() =>
        """
        Analizo la red de dependencias de la base seleccionada. Puede preguntarme:

        - **Impacto:** "¿qué pasa si modifico `dbo.Clientes`?" o seleccione un nodo y pregunte "¿qué rompe este?"
        - **Detalle:** "¿qué hace `sp_GenerarPedido`?", "¿quién usa `dbo.Pagos`?"
        - **Puntos únicos de fallo:** "¿cuáles son los puntos críticos?"
        - **Acoplamiento:** "¿qué objetos están más acoplados?"
        - **Redundancias:** "¿hay SPs duplicados?"
        - **Cuellos de botella:** "¿dónde puede haber bloqueos?"
        - **Referencias rotas:** "¿hay referencias rotas?"
        - **Resumen:** "dame un diagnóstico general"
        """;

    public string AskForObject(IEnumerable<string> suggestions) =>
        $"No identifiqué el objeto de la pregunta. Escriba su nombre (p. ej. `dbo.Tabla` o `sp_Nombre`) o selecciónelo en el grafo. Objetos más referenciados: {JoinNames(suggestions)}.";

    // ---- Etiquetas --------------------------------------------------------------------------

    public static string Label(NodeKind kind) => kind switch
    {
        NodeKind.Table => "tabla",
        NodeKind.View => "vista",
        NodeKind.StoredProcedure => "procedimiento",
        NodeKind.Function => "función",
        NodeKind.Trigger => "trigger",
        NodeKind.Synonym => "sinónimo",
        NodeKind.External => "objeto externo",
        NodeKind.LinkedServer => "servidor vinculado",
        NodeKind.Missing => "objeto inexistente",
        _ => kind.ToString()
    };

    public static string Label(RiskLevel level) => level switch
    {
        RiskLevel.Low => "Bajo",
        RiskLevel.Medium => "Medio",
        RiskLevel.High => "Alto",
        RiskLevel.Critical => "Crítico",
        _ => level.ToString()
    };

    public static string Relation(DependencyKind kinds)
    {
        List<string> verbs = new();
        if ((kinds & DependencyKind.Writes) != 0) verbs.Add("escribe");
        if ((kinds & DependencyKind.Reads) != 0) verbs.Add("lee");
        if ((kinds & DependencyKind.Executes) != 0) verbs.Add("ejecuta");
        if ((kinds & DependencyKind.Calls) != 0) verbs.Add("llama");
        if ((kinds & DependencyKind.TriggeredBy) != 0) verbs.Add("trigger");
        if ((kinds & DependencyKind.SynonymFor) != 0) verbs.Add("sinónimo");
        if (verbs.Count == 0 && (kinds & DependencyKind.References) != 0) verbs.Add("referencia");
        return string.Join("+", verbs);
    }

    private static IEnumerable<string> Recommendations(ImpactReport report)
    {
        switch (report.Level)
        {
            case RiskLevel.Critical:
            case RiskLevel.High:
                yield return "Preferir cambios compatibles hacia atrás (agregar columnas NULL, no renombrar ni cambiar tipos).";
                yield return "Probar en UAT al menos los objetos directos y los críticos listados.";
                if (report.Affected.Any(a => a.IsOtherDatabase))
                {
                    yield return "Coordinar con los dueños de las otras bases afectadas antes del despliegue.";
                }
                yield return "Preparar script de reversión y desplegar en ventana controlada.";
                break;
            case RiskLevel.Medium:
                yield return "Revisar los objetos directos y ejecutar sus pruebas antes del cambio.";
                break;
            default:
                yield return "Riesgo bajo: validar los objetos directos (si los hay) y continuar.";
                break;
        }
    }

    private static void AppendWarnings(StringBuilder md, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }
        md.AppendLine();
        md.AppendLine("_Límites del análisis:_");
        foreach (string warning in warnings)
        {
            md.AppendLine($"- _{warning}_");
        }
    }

    private static string JoinWithRelation(IReadOnlyList<ImpactedObject> items) =>
        string.Join(", ", items.Take(MaxListed).Select(i => $"`{i.Name}` ({Relation(i.Relation)})")) + More(items.Count);

    private static string JoinNames(IEnumerable<string> names)
    {
        List<string> list = names.ToList();
        return string.Join(", ", list.Take(MaxListed).Select(n => $"`{n}`")) + More(list.Count);
    }

    private static string More(int count) => count > MaxListed ? $" y {count - MaxListed} más" : string.Empty;
}
