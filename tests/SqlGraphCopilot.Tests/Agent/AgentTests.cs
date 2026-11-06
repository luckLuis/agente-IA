using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Agent;
using SqlGraphCopilot.Tests.Fakes;

namespace SqlGraphCopilot.Tests.Agent;

public sealed class AgentTests
{
    private const string Dev = TestEngine.Dev;

    private static FakeCatalog Catalog()
    {
        FakeCatalog catalog = new();
        FakeDatabase db = catalog.Database(Dev, "Ventas");
        int clientes = db.Table("Clientes");
        int generar = db.Proc("sp_GenerarPedido", "CREATE PROCEDURE dbo.sp_GenerarPedido AS SELECT * FROM dbo.Clientes;");
        int consultar = db.Proc("sp_ConsultarCliente", "CREATE PROCEDURE dbo.sp_ConsultarCliente AS SELECT * FROM dbo.Clientes;");
        db.Ref(generar, clientes).Ref(consultar, clientes);
        return catalog;
    }

    [Theory]
    [InlineData("¿Qué pasa si modifico dbo.Clientes?", AgentIntent.Impact)]
    [InlineData("¿Hay SPs duplicados?", AgentIntent.Redundancy)]
    [InlineData("¿Dónde puede haber bloqueos?", AgentIntent.Bottlenecks)]
    [InlineData("¿Cuáles son los puntos únicos de fallo?", AgentIntent.SinglePointsOfFailure)]
    [InlineData("¿Hay referencias rotas?", AgentIntent.BrokenReferences)]
    [InlineData("Dame un diagnóstico general", AgentIntent.Overview)]
    [InlineData("¿Qué hace sp_X?", AgentIntent.Explain)]
    public void Clasifica_intencion(string message, AgentIntent expected)
    {
        Assert.Equal(expected, new IntentClassifier().Classify(message));
    }

    [Fact]
    public async Task Impacto_por_nombre_sin_llm()
    {
        TestEngine engine = new(Catalog());
        AgentResponse response = await engine.Agent.AskAsync(new AgentRequest(Dev, "Ventas", "¿Qué pasa si modifico la tabla clientes?", null), CancellationToken.None);

        Assert.Equal(AgentIntent.Impact, response.Intent);
        Assert.Equal("deterministic", response.Engine);
        Assert.Contains("dbo.Clientes", response.Reply);
        Assert.Contains("dbo.sp_GenerarPedido", response.Reply);
        Assert.Contains(TestEngine.Id(Dev, "Ventas", "dbo", "Clientes"), response.FocusNodeIds);
    }

    [Fact]
    public async Task Usa_el_nodo_seleccionado_cuando_dice_este()
    {
        TestEngine engine = new(Catalog());
        string focus = TestEngine.Id(Dev, "Ventas", "dbo", "sp_GenerarPedido");
        AgentResponse response = await engine.Agent.AskAsync(new AgentRequest(Dev, "Ventas", "¿qué rompe este?", focus), CancellationToken.None);

        Assert.Equal(focus, response.FocusNodeIds[0]);
        Assert.Contains("sp_GenerarPedido", response.Reply);
    }

    [Fact]
    public async Task Sin_objeto_reconocible_pide_aclaracion_con_sugerencias()
    {
        TestEngine engine = new(Catalog());
        AgentResponse response = await engine.Agent.AskAsync(new AgentRequest(Dev, "Ventas", "¿Qué pasa si modifico eso?", null), CancellationToken.None);

        Assert.Contains("No identifiqué el objeto", response.Reply);
        Assert.Contains("dbo.Clientes", response.Reply);
    }

    [Fact]
    public async Task Si_el_llm_falla_responde_con_el_narrador()
    {
        TestEngine engine = new(Catalog(), new FailingLlm());
        AgentResponse response = await engine.Agent.AskAsync(new AgentRequest(Dev, "Ventas", "impacto de dbo.Clientes", null), CancellationToken.None);

        Assert.Equal("deterministic-fallback", response.Engine);
        Assert.Contains("dbo.Clientes", response.Reply);
    }

    [Fact]
    public async Task El_llm_recibe_hechos_y_nunca_codigo_sql()
    {
        CapturingLlm llm = new();
        TestEngine engine = new(Catalog(), llm);
        AgentResponse response = await engine.Agent.AskAsync(new AgentRequest(Dev, "Ventas", "¿Qué pasa si modifico dbo.Clientes?", null), CancellationToken.None);

        Assert.Equal("respuesta del modelo", response.Reply);
        Assert.Contains("dbo.sp_GenerarPedido", llm.LastPrompt!.User);
        Assert.DoesNotContain("CREATE PROCEDURE", llm.LastPrompt.User);
        Assert.Contains("Usa SOLO los hechos", llm.LastPrompt.System);
    }

    private sealed class FailingLlm : ILlmClient
    {
        public string Name => "failing";
        public bool IsEnabled => true;
        public Task<string> CompleteAsync(LlmPrompt prompt, CancellationToken cancellationToken) => throw new HttpRequestException("caído");
    }

    private sealed class CapturingLlm : ILlmClient
    {
        public LlmPrompt? LastPrompt { get; private set; }
        public string Name => "capturing";
        public bool IsEnabled => true;
        public Task<string> CompleteAsync(LlmPrompt prompt, CancellationToken cancellationToken)
        {
            LastPrompt = prompt;
            return Task.FromResult("respuesta del modelo");
        }
    }
}
