using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlGraphCopilot.Application.Agent;
using SqlGraphCopilot.Application.Analysis;
using SqlGraphCopilot.Application.Exploration;
using SqlGraphCopilot.Application.Flow;
using SqlGraphCopilot.Application.Indexing;

namespace SqlGraphCopilot.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Registra los casos de uso de la capa Application: índice, clasificador, exploración,
    /// análisis y agente. Los puertos (ICatalogReader, ILlmClient, ...) los implementa Infrastructure.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddOptions();

        // Índice
        services.TryAddSingleton<DatabaseIndexBuilder>();
        services.TryAddSingleton<IDatabaseIndexProvider, DatabaseIndexCache>();
        services.TryAddSingleton<DependencyClassifier>();

        // Exploración y análisis
        services.TryAddSingleton<GraphExplorer>();
        services.TryAddSingleton<ImpactAnalyzer>();
        services.TryAddSingleton<StructuralAnalyzer>();

        // Agente
        services.TryAddSingleton<IntentClassifier>();
        services.TryAddSingleton<EntityResolver>();
        services.TryAddSingleton<DeterministicNarrator>();
        services.TryAddSingleton<PromptBuilder>();
        services.TryAddSingleton<SqlCopilotAgent>();
        services.TryAddSingleton<ISqlGraphToolbox, SqlGraphToolbox>();

        // Caché de respuestas del LLM (mismo prompt = cero tokens). Opciones: sección "LlmCache" (se enlaza en AddInfrastructure).
        services.TryAddSingleton<LlmResponseCache>();

        // Datos de entrada de un SP (análisis de flujo)
        services.TryAddSingleton<ProcedureFlowService>();

        return services;
    }
}
