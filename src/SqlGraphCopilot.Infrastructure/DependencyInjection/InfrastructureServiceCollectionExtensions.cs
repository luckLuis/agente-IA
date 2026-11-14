using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.DependencyInjection;
using SqlGraphCopilot.Application.Agent;
using SqlGraphCopilot.Application.Migration;
using SqlGraphCopilot.Application.Settings;
using SqlGraphCopilot.Application.Usage;
using SqlGraphCopilot.Infrastructure.Flow;
using SqlGraphCopilot.Infrastructure.Instances;
using SqlGraphCopilot.Infrastructure.Llm;
using SqlGraphCopilot.Infrastructure.Migration;
using SqlGraphCopilot.Infrastructure.Parsing;
using SqlGraphCopilot.Infrastructure.SqlServer;
using SqlGraphCopilot.Infrastructure.Usage;
using SqlGraphCopilot.Application.Mishi;
using SqlGraphCopilot.Infrastructure.Mishi;

namespace SqlGraphCopilot.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registro completo del motor (Application + Infrastructure).
    /// Uso: <c>services.AddSqlGraphCopilot(configuration);</c>
    /// Conserva la API pública que consumía el agente del core cuando todo vivía en un solo proyecto.
    /// </summary>
    public static IServiceCollection AddSqlGraphCopilot(this IServiceCollection services, IConfiguration configuration) =>
        services.AddApplication().AddInfrastructure(configuration);

    /// <summary>
    /// Registra las implementaciones concretas de los puertos de Application: SQL Server, ScriptDom y LLM.
    /// Lee las secciones "SqlGraphCopilot" y "SqlGraphCopilot:Llm".
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
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
        services.Configure<MishiOptions>(configuration.GetSection("Mishi"));
        services.TryAddSingleton<IRiskScanner, RiskScanner>();

        // Parsing T-SQL
        services.TryAddSingleton<ITSqlParsingService, TSqlParsingService>();

        // LLM
        services.AddHttpClient(OpenAiCompatibleLlmClient.HttpClientName, client =>
        {
            // El timeout real lo controla LlmHttp con LlmOptions.TimeoutSeconds.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.TryAddSingleton<ILlmClient>(sp =>
        {
            LlmOptions llm = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            return llm.Provider switch
            {
                LlmProvider.OpenAICompatible => ActivatorUtilities.CreateInstance<OpenAiCompatibleLlmClient>(sp),
                LlmProvider.Anthropic => ActivatorUtilities.CreateInstance<AnthropicLlmClient>(sp),
                LlmProvider.FoundryAgent => ActivatorUtilities.CreateInstance<FoundryAgentLlmClient>(sp),
                _ => new DisabledLlmClient()
            };
        });

        // Análisis de flujo de un SP (ScriptDom)
        services.TryAddSingleton<IProcedureFlowAnalyzer, ScriptDomProcedureFlowAnalyzer>();

        // Validación del código C# migrado (solo desarrollo)
        services.Configure<MigrationVerifierOptions>(configuration.GetSection("MigrationVerifier"));
        services.TryAddSingleton<IMigrationVerifier, MigrationVerifier>();

        // Estadísticas de uso (tokens, tiempo, ahorro, calidad)
        services.Configure<UsageOptions>(configuration.GetSection("Usage"));
        services.TryAddSingleton<IUsageTracker, UsageTracker>();

        // Opciones de la caché de respuestas del LLM (la caché en sí la registra AddApplication)
        services.Configure<LlmResponseCacheOptions>(configuration.GetSection("LlmCache"));

        return services;
    }
}
