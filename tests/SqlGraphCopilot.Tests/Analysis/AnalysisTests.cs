using SqlGraphCopilot.Application.Analysis;
using SqlGraphCopilot.Domain;
using SqlGraphCopilot.Domain.Indexing;
using SqlGraphCopilot.Tests.Fakes;

namespace SqlGraphCopilot.Tests.Analysis;

public sealed class AnalysisTests
{
    private const string Dev = TestEngine.Dev;

    private static string Id(string name, string database = "Ventas") => TestEngine.Id(Dev, database, "dbo", name);

    /// <summary>
    /// sp_GenerarPedido escribe Pedidos y lee Clientes; sp_Facturar lee Pedidos; sp_Cierre ejecuta sp_Facturar;
    /// sp_Pagar escribe Pedidos (2 escritores); sp_Copia es idéntico a sp_Facturar; sp_A ⇄ sp_B en ciclo;
    /// cadena sp_N1 → sp_N2 → sp_N3 → sp_N4 → sp_N5; sp_Roto referencia dbo.NoExiste.
    /// </summary>
    private static TestEngine Scenario()
    {
        FakeCatalog catalog = new();
        FakeDatabase db = catalog.Database(Dev, "Ventas");

        int clientes = db.Table("Clientes");
        int pedidos = db.Table("Pedidos");
        const string facturarSql = "CREATE PROCEDURE dbo.sp_Facturar AS SELECT * FROM dbo.Pedidos;";

        int generar = db.Proc("sp_GenerarPedido", "CREATE PROCEDURE dbo.sp_GenerarPedido AS INSERT INTO dbo.Pedidos (ClienteId) SELECT Id FROM dbo.Clientes;");
        int facturar = db.Proc("sp_Facturar", facturarSql);
        int copia = db.Proc("sp_Copia", facturarSql);
        int cierre = db.Proc("sp_Cierre", "CREATE PROCEDURE dbo.sp_Cierre AS EXEC dbo.sp_Facturar;");
        int pagar = db.Proc("sp_Pagar", "CREATE PROCEDURE dbo.sp_Pagar AS UPDATE dbo.Pedidos SET Pagado = 1;");

        db.Ref(generar, pedidos).Ref(generar, clientes)
          .Ref(facturar, pedidos)
          .Ref(copia, pedidos)
          .Ref(cierre, facturar)
          .Ref(pagar, pedidos);

        int a = db.Proc("sp_A", "CREATE PROCEDURE dbo.sp_A AS EXEC dbo.sp_B;");
        int b = db.Proc("sp_B", "CREATE PROCEDURE dbo.sp_B AS EXEC dbo.sp_A;");
        db.Ref(a, b).Ref(b, a);

        int[] chain = Enumerable.Range(1, 5).Select(i => db.Proc($"sp_N{i}")).ToArray();
        for (int i = 0; i < chain.Length - 1; i++)
        {
            db.Ref(chain[i], chain[i + 1]);
        }

        int roto = db.Proc("sp_Roto", "CREATE PROCEDURE dbo.sp_Roto AS SELECT * FROM dbo.NoExiste;");
        db.RefUnresolved(roto, "dbo", "NoExiste");

        // Otra base que usa Ventas.dbo.Pedidos (impacto cross-database)
        FakeDatabase reportes = catalog.Database(Dev, "Reportes");
        int rep = reportes.Proc("sp_ReportePedidos", "CREATE PROCEDURE dbo.sp_ReportePedidos AS SELECT * FROM Ventas.dbo.Pedidos;");
        reportes.RefExternal(rep, null, "Ventas", "dbo", "Pedidos");

        return new TestEngine(catalog);
    }

    [Fact]
    public async Task Impacto_estructural_transitivo_y_en_otras_bases()
    {
        TestEngine engine = Scenario();
        await engine.IndexAsync(Dev, "Reportes"); // base ya cargada que referencia Pedidos

        ImpactReport report = await engine.Impact.AnalyzeAsync(Id("Pedidos"), 6, CancellationToken.None);

        Assert.Contains(report.Affected, a => a.NodeId == Id("sp_Facturar") && a.Depth == 1);
        Assert.Contains(report.Affected, a => a.NodeId == Id("sp_Cierre") && a.Depth == 2);
        Assert.Contains(report.Affected, a => a.IsOtherDatabase && a.Name.Contains("sp_ReportePedidos"));
        Assert.Contains("DEV/Reportes", report.SearchedDatabases);
        Assert.True(report.CriticalAffected >= 1); // sp_Cierre coincide con el patrón "Cierre"
        Assert.NotEqual(RiskLevel.Low, report.Level);
    }

    [Fact]
    public async Task Impacto_por_datos_lista_lectores_de_lo_que_escribe()
    {
        TestEngine engine = Scenario();
        DatabaseIndex index = await engine.IndexAsync(Dev, "Ventas");
        await engine.Classifier.DeepScanAsync(index, CancellationToken.None);

        ImpactReport report = engine.Impact.Analyze(index, Id("sp_GenerarPedido"), 6, Array.Empty<DatabaseIndex>());

        DataImpactEntry pedidos = Assert.Single(report.DataImpact);
        Assert.Contains(Id("sp_Facturar"), pedidos.ReaderIds);
        Assert.DoesNotContain(Id("sp_GenerarPedido"), pedidos.ReaderIds);
    }

    [Fact]
    public async Task Escaneo_profundo_clasifica_todo()
    {
        TestEngine engine = Scenario();
        DatabaseIndex index = await engine.IndexAsync(Dev, "Ventas");

        var result = await engine.Classifier.DeepScanAsync(index, CancellationToken.None);

        Assert.Equal(1.0, result.ClassificationCoverage);
        Assert.True(index.DeepScanCompleted);
        Assert.True(index.Graph.GetOutgoing(Id("sp_GenerarPedido")).Single(e => e.TargetId == Id("Pedidos")).Has(DependencyKind.Writes));
    }

    [Fact]
    public async Task Cuellos_de_botella_ciclos_cadenas_y_escrituras_concurrentes()
    {
        TestEngine engine = Scenario();
        DatabaseIndex index = await engine.IndexAsync(Dev, "Ventas");
        await engine.Classifier.DeepScanAsync(index, CancellationToken.None);

        BottleneckReport report = engine.Structural.GetBottlenecks(index);

        WriteHotspot hotspot = Assert.Single(report.WriteHotspots);
        Assert.Equal(Id("Pedidos"), hotspot.TableId);
        Assert.Equal(2, hotspot.Writers);

        CycleFinding cycle = Assert.Single(report.Cycles);
        Assert.Equal(2, cycle.NodeIds.Count);

        CallChain chain = Assert.Single(report.DeepCallChains);
        Assert.Equal(4, chain.Depth);
        Assert.Equal("dbo.sp_N1", chain.Path[0]);
    }

    [Fact]
    public async Task Redundancias_detecta_definiciones_identicas()
    {
        TestEngine engine = Scenario();
        DatabaseIndex index = await engine.IndexAsync(Dev, "Ventas");

        RedundancyReport report = engine.Structural.GetRedundancies(index);

        DuplicateGroup group = Assert.Single(report.ExactDuplicates);
        Assert.Contains("dbo.sp_Facturar", group.Names);
        Assert.Contains("dbo.sp_Copia", group.Names);
    }

    [Fact]
    public async Task Puntos_unicos_de_fallo_y_referencias_rotas()
    {
        TestEngine engine = Scenario();
        DatabaseIndex index = await engine.IndexAsync(Dev, "Ventas");

        IReadOnlyList<SpofFinding> spof = engine.Structural.GetSinglePointsOfFailure(index);
        Assert.Equal(Id("Pedidos"), spof[0].NodeId);

        BrokenReference broken = Assert.Single(engine.Structural.GetBrokenReferences(index));
        Assert.Equal("dbo.NoExiste", broken.MissingName);
        Assert.Contains("dbo.sp_Roto", broken.ReferencedByNames);
    }

    [Fact]
    public async Task Los_analisis_se_cachean_hasta_que_el_grafo_cambia()
    {
        TestEngine engine = Scenario();
        DatabaseIndex index = await engine.IndexAsync(Dev, "Ventas");

        BottleneckReport first = engine.Structural.GetBottlenecks(index);
        Assert.Same(first, engine.Structural.GetBottlenecks(index));

        await engine.Classifier.EnsureClassifiedAsync(index, Id("sp_Pagar"), CancellationToken.None);
        Assert.NotSame(first, engine.Structural.GetBottlenecks(index));
    }
}
