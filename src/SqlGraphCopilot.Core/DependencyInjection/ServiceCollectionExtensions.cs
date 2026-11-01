using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SqlGraphCopilot.Core.Agent;
using SqlGraphCopilot.Core.Agent.Llm;
using SqlGraphCopilot.Core.Analysis;
using SqlGraphCopilot.Core.Exploration;
using SqlGraphCopilot.Core.Indexing;
using SqlGraphCopilot.Core.Metadata;
using SqlGraphCopilot.Core.Parsing;

namespace SqlGraphCopilot.Core.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra el motor completo. Uso: <c>services.AddSqlGraphCopilot(configuration);</c>
    /// Lee las secciones "SqlGraphCopilot" y "SqlGraphCopilot:Llm".
    /// </summary>
    public static IServiceCollection AddSqlGraphCopilot(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SqlGraphCopilotOptions>()
            .Bind(configuration.GetSection(SqlGraphCopilotOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SqlGraphCopilotOptions>, SqlGraphCopilotOptionsValidator>());

        services.AddOptions<LlmOptions>()
            .Bind(configuration.GetSection(LlmOptions.SectionName));

        // Metadata (fuente real; sin mocks)
        services.TryAddSingleton<IInstanceRegistry, InstanceRegistry>();
        services.TryAddSingleton<IDbConnectionFactory, SqlServerConnectionFactory>();
        services.TryAddSingleton<ICatalogReader, SqlServerCatalogReader>();

        // Parsing e índice
        services.TryAddSingleton<ITSqlParsingService, TSqlParsingService>();
        services.TryAddSingleton<DatabaseIndexBuilder>();
        services.TryAddSingleton<IDatabaseIndexProvider, DatabaseIndexCache>();
        services.TryAddSingleton<DependencyClassifier>();

        // Exploración y análisis
        services.TryAddSingleton<GraphExplorer>();
        services.TryAddSingleton<ImpactAnalyzer>();
        services.TryAddSingleton<StructuralAnalyzer>();

        // Agente
        services.AddHttpClient(OpenAiCompatibleLlmClient.HttpClientName, client =>
        {
            // El timeout real lo controla LlmHttp con LlmOptions.TimeoutSeconds.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.TryAddSingleton<ILlmClient>(sp =>
        {
            LlmOptions llm = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            return llm.Provider switch
            {
                LlmProvider.OpenAICompatible => ActivatorUtilities.CreateInstance<OpenAiCompatibleLlmClient>(sp),
                LlmProvider.Anthropic => ActivatorUtilities.CreateInstance<AnthropicLlmClient>(sp),
                _ => new DisabledLlmClient()
            };
        });
        services.TryAddSingleton<IntentClassifier>();
        services.TryAddSingleton<EntityResolver>();
        services.TryAddSingleton<DeterministicNarrator>();
        services.TryAddSingleton<PromptBuilder>();
        services.TryAddSingleton<SqlCopilotAgent>();
        services.TryAddSingleton<ISqlGraphToolbox, SqlGraphToolbox>();

        return services;
    }
}
