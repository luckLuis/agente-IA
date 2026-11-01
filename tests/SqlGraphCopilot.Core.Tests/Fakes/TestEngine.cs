using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlGraphCopilot.Core.Agent;
using SqlGraphCopilot.Core.Agent.Llm;
using SqlGraphCopilot.Core.Analysis;
using SqlGraphCopilot.Core.Exploration;
using SqlGraphCopilot.Core.Indexing;
using SqlGraphCopilot.Core.Metadata;
using SqlGraphCopilot.Core.Parsing;

namespace SqlGraphCopilot.Core.Tests.Fakes;

/// <summary>Arma el motor completo con el catálogo en memoria (sin contenedor DI, para pruebas rápidas).</summary>
internal sealed class TestEngine
{
    public const string Dev = "DEV";
    public const string Other = "OTHER";

    public TestEngine(FakeCatalog catalog, ILlmClient? llm = null, Action<SqlGraphCopilotOptions>? configure = null)
    {
        SqlGraphCopilotOptions options = new()
        {
            Instances =
            {
                new SqlInstanceOptions { Name = Dev, ConnectionString = "Server=fake", LinkedServerAliases = { "SRVDEV01" } },
                new SqlInstanceOptions { Name = Other, ConnectionString = "Server=fake", LinkedServerAliases = { "LNK_OTHER" } }
            },
            CriticalNamePatterns = { "Clientes", "Cierre" }
        };
        configure?.Invoke(options);
        IOptions<SqlGraphCopilotOptions> wrapped = Options.Create(options);

        Catalog = catalog;
        Registry = new InstanceRegistry(wrapped);
        Parser = new TSqlParsingService();
        Builder = new DatabaseIndexBuilder(Registry, wrapped);
        Indexes = new DatabaseIndexCache(catalog, Builder, Registry, wrapped, NullLogger<DatabaseIndexCache>.Instance);
        Classifier = new DependencyClassifier(catalog, Parser, Registry, NullLogger<DependencyClassifier>.Instance);
        Explorer = new GraphExplorer(Indexes, Classifier, Registry, wrapped);
        Impact = new ImpactAnalyzer(Indexes, Registry);
        Structural = new StructuralAnalyzer();
        Agent = new SqlCopilotAgent(
            Indexes, Classifier, Impact, Structural,
            new IntentClassifier(), new EntityResolver(), new DeterministicNarrator(), new PromptBuilder(),
            llm ?? new DisabledLlmClient(),
            catalog,                                        // ICatalogReader (faltaba)
            NullLogger<SqlCopilotAgent>.Instance);
    }

    public FakeCatalog Catalog { get; }
    public InstanceRegistry Registry { get; }
    public TSqlParsingService Parser { get; }
    public DatabaseIndexBuilder Builder { get; }
    public DatabaseIndexCache Indexes { get; }
    public DependencyClassifier Classifier { get; }
    public GraphExplorer Explorer { get; }
    public ImpactAnalyzer Impact { get; }
    public StructuralAnalyzer Structural { get; }
    public SqlCopilotAgent Agent { get; }

    public Task<DatabaseIndex> IndexAsync(string instance, string database) =>
        Indexes.GetAsync(instance, database, CancellationToken.None);

    public static string Id(string instance, string database, string schema, string name) =>
        new Domain.ObjectKey(instance, database, schema, name).Id;
}
