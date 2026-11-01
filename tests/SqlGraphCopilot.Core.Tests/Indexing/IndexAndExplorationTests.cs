using SqlGraphCopilot.Core.Domain;
using SqlGraphCopilot.Core.Exploration;
using SqlGraphCopilot.Core.Indexing;
using SqlGraphCopilot.Core.Tests.Fakes;

namespace SqlGraphCopilot.Core.Tests.Indexing;

public sealed class IndexAndExplorationTests
{
    private const string Dev = TestEngine.Dev;
    private const string Other = TestEngine.Other;

    /// <summary>
    /// Core (DEV): sp_Sincronizar lee Clientes, escribe Inventario.dbo.Stock (otra base, misma instancia)
    /// y LNK_OTHER.Remota.dbo.Saldos (servidor vinculado registrado como OTHER); además referencia
    /// LNK_DESCONOCIDO.X.dbo.Y (no registrado) y dbo.TablaBorrada (no existe).
    /// </summary>
    private static (TestEngine Engine, string SyncId, string ClientesId) Scenario()
    {
        FakeCatalog catalog = new();
        FakeDatabase core = catalog.Database(Dev, "Core");
        int clientes = core.Table("Clientes");
        int sync = core.Proc("sp_Sincronizar", """
            CREATE PROCEDURE dbo.sp_Sincronizar AS
            BEGIN
              SELECT * FROM dbo.Clientes;
              UPDATE Inventario.dbo.Stock SET Cantidad = 0;
              UPDATE LNK_OTHER.Remota.dbo.Saldos SET Valor = 0;
              SELECT * FROM LNK_DESCONOCIDO.X.dbo.Y;
              SELECT * FROM dbo.TablaBorrada;
            END
            """);
        core.Ref(sync, clientes)
            .RefExternal(sync, null, "Inventario", "dbo", "Stock")
            .RefExternal(sync, "LNK_OTHER", "Remota", "dbo", "Saldos")
            .RefExternal(sync, "LNK_DESCONOCIDO", "X", "dbo", "Y")
            .RefUnresolved(sync, "dbo", "TablaBorrada");

        int trigger = core.Trigger("tr_Clientes_Auditoria", clientes, "CREATE TRIGGER dbo.tr_Clientes_Auditoria ON dbo.Clientes AFTER UPDATE AS SELECT 1;");
        _ = trigger;
        core.Synonym("syn_Stock", null, "Inventario", "dbo", "Stock");

        FakeDatabase inventario = catalog.Database(Dev, "Inventario");
        int stock = inventario.Table("Stock");
        int actualizar = inventario.Proc("sp_ActualizarStock", "CREATE PROCEDURE dbo.sp_ActualizarStock AS UPDATE dbo.Stock SET Cantidad = 1;");
        inventario.Ref(actualizar, stock);

        FakeDatabase remota = catalog.Database(Other, "Remota");
        remota.Table("Saldos");

        TestEngine engine = new(catalog);
        return (engine, TestEngine.Id(Dev, "Core", "dbo", "sp_Sincronizar"), TestEngine.Id(Dev, "Core", "dbo", "Clientes"));
    }

    [Fact]
    public async Task Construye_nodos_externos_rotos_triggers_y_sinonimos()
    {
        (TestEngine engine, string syncId, string clientesId) = Scenario();
        DatabaseIndex index = await engine.IndexAsync(Dev, "Core");

        GraphNode stock = index.Graph.GetNode(TestEngine.Id(Dev, "Inventario", "dbo", "Stock"))!;
        Assert.Equal(NodeKind.External, stock.Kind);
        Assert.Equal(ExternalScope.CrossDatabase, stock.Scope);

        // El alias del linked server se traduce a la instancia registrada: el Id coincide con el índice real.
        GraphNode saldos = index.Graph.GetNode(TestEngine.Id(Other, "Remota", "dbo", "Saldos"))!;
        Assert.Equal(ExternalScope.LinkedServer, saldos.Scope);

        GraphNode desconocido = index.Graph.GetNode(TestEngine.Id("LNK_DESCONOCIDO", "X", "dbo", "Y"))!;
        Assert.Equal(ExternalScope.LinkedServer, desconocido.Scope);

        Assert.Equal(NodeKind.Missing, index.Graph.GetNode(TestEngine.Id(Dev, "Core", "dbo", "TablaBorrada"))!.Kind);

        Assert.Contains(index.Graph.GetIncoming(clientesId), e => e.Has(DependencyKind.TriggeredBy));
        Assert.Contains(index.Graph.GetOutgoing(TestEngine.Id(Dev, "Core", "dbo", "syn_Stock")), e => e.Has(DependencyKind.SynonymFor));

        Assert.True(index.Graph.GetNode(clientesId)!.IsCritical);
        Assert.Equal(1, index.GetSummary().BrokenReferenceCount);
        Assert.Contains(index.Graph.GetOutgoing(syncId), e => e.TargetId == clientesId);
    }

    [Fact]
    public async Task Clasificador_convierte_referencias_en_lectura_y_escritura()
    {
        (TestEngine engine, string syncId, string clientesId) = Scenario();
        DatabaseIndex index = await engine.IndexAsync(Dev, "Core");

        Assert.All(index.Graph.GetOutgoing(syncId), e => Assert.True(e.Has(DependencyKind.References)));

        Assert.True(await engine.Classifier.EnsureClassifiedAsync(index, syncId, CancellationToken.None));

        GraphEdge toClientes = index.Graph.GetOutgoing(syncId).Single(e => e.TargetId == clientesId);
        Assert.True(toClientes.Has(DependencyKind.Reads));
        Assert.False(toClientes.Has(DependencyKind.Writes));

        GraphEdge toStock = index.Graph.GetOutgoing(syncId).Single(e => e.TargetId == TestEngine.Id(Dev, "Inventario", "dbo", "Stock"));
        Assert.True(toStock.Has(DependencyKind.Writes));

        GraphEdge toSaldos = index.Graph.GetOutgoing(syncId).Single(e => e.TargetId == TestEngine.Id(Other, "Remota", "dbo", "Saldos"));
        Assert.True(toSaldos.Has(DependencyKind.Writes));

        Assert.True(index.Graph.GetNode(syncId)!.IsClassified);
    }

    [Fact]
    public async Task Hubs_devuelve_solo_objetos_locales_mas_referenciados()
    {
        (TestEngine engine, _, string clientesId) = Scenario();
        ExplorationResult hubs = await engine.Explorer.GetHubsAsync(Dev, "Core", 2, CancellationToken.None);

        Assert.Equal(ExplorationStatus.Ok, hubs.Status);
        Assert.Equal(2, hubs.Subgraph.Nodes.Count);
        Assert.Contains(hubs.Subgraph.Nodes, n => n.Id == clientesId);
        Assert.All(hubs.Subgraph.Nodes, n => Assert.False(n.IsExternal));
    }

    [Fact]
    public async Task Expandir_nodo_externo_carga_solo_la_rama_de_su_base()
    {
        (TestEngine engine, string syncId, _) = Scenario();
        await engine.Explorer.GetNeighborhoodAsync(syncId, 1, TraversalDirection.Both, null, CancellationToken.None);
        int readsBefore = engine.Catalog.CatalogReads;

        string stockId = TestEngine.Id(Dev, "Inventario", "dbo", "Stock");
        ExplorationResult branch = await engine.Explorer.GetNeighborhoodAsync(stockId, 1, TraversalDirection.Both, null, CancellationToken.None);

        Assert.Equal(ExplorationStatus.Ok, branch.Status);
        Assert.Equal("Inventario", branch.Database);
        GraphNode realStock = branch.Subgraph.Nodes.Single(n => n.Id == stockId);
        Assert.Equal(NodeKind.Table, realStock.Kind);                    // ya no es "externo"
        Assert.Contains(branch.Subgraph.Nodes, n => n.Key.Name == "sp_ActualizarStock");
        Assert.Equal(readsBefore + 1, engine.Catalog.CatalogReads);     // se leyó solo la base Inventario
    }

    [Fact]
    public async Task Servidor_vinculado_no_registrado_es_inalcanzable()
    {
        (TestEngine engine, _, _) = Scenario();
        ExplorationResult result = await engine.Explorer.GetNeighborhoodAsync(
            TestEngine.Id("LNK_DESCONOCIDO", "X", "dbo", "Y"), 1, TraversalDirection.Both, null, CancellationToken.None);

        Assert.Equal(ExplorationStatus.Unreachable, result.Status);
        Assert.Contains("no está registrado", result.Message);
    }

    [Fact]
    public async Task El_indice_se_cachea_y_se_invalida()
    {
        (TestEngine engine, _, _) = Scenario();
        await engine.IndexAsync(Dev, "Core");
        await engine.IndexAsync(Dev, "Core");
        Assert.Equal(1, engine.Catalog.CatalogReads);

        engine.Indexes.Invalidate(Dev, "Core");
        await engine.IndexAsync(Dev, "Core");
        Assert.Equal(2, engine.Catalog.CatalogReads);
    }

    [Fact]
    public async Task Busqueda_paginada_y_por_tipo()
    {
        (TestEngine engine, _, _) = Scenario();
        DatabaseIndex index = await engine.IndexAsync(Dev, "Core");

        PagedResult<GraphNode> procs = index.Search(new ObjectSearchQuery("sp_", new[] { NodeKind.StoredProcedure }, false, 1, 10));
        Assert.Equal("sp_Sincronizar", Assert.Single(procs.Items).Key.Name);

        PagedResult<GraphNode> page = index.Search(new ObjectSearchQuery(null, null, false, 1, 2));
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.TotalPages >= 2);
    }
}
