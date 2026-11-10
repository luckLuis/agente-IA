namespace SqlGraphCopilot.Infrastructure.Llm;

public enum LlmProvider
{
    /// <summary>Sin LLM: el agente responde con el narrador determinista (funciona sin red).</summary>
    None,

    /// <summary>API compatible con OpenAI Chat Completions: Azure OpenAI, OpenAI, Ollama, LM Studio, vLLM.</summary>
    OpenAICompatible,

    /// <summary>Claude (Anthropic Messages API).</summary>
    Anthropic,

    FoundryAgent
}

public sealed class LlmOptions
{
    public const string SectionName = "SqlGraphCopilot:Llm";

    public LlmProvider Provider { get; set; } = LlmProvider.None;

    /// <summary>
    /// URL completa del endpoint. Ejemplos:
    ///  - Azure OpenAI: https://{recurso}.openai.azure.com/openai/deployments/{deployment}/chat/completions?api-version=2024-06-01
    ///  - Ollama local: http://localhost:11434/v1/chat/completions
    ///  - Anthropic: https://api.anthropic.com/v1/messages (valor por defecto)
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>No la ponga en appsettings: use user-secrets, variable de entorno o Key Vault.</summary>
    public string? ApiKey { get; set; }

    /// <summary>"Authorization" (Bearer) o "api-key" (Azure OpenAI).</summary>
    public string ApiKeyHeader { get; set; } = "Authorization";

    public string? Model { get; set; }
    public int MaxTokens { get; set; } = 1200;

    /// <summary>Null = no se envía (algunos modelos nuevos no aceptan temperature).</summary>
    public double? Temperature { get; set; }

    /// <summary>Modelos de razonamiento de OpenAI usan max_completion_tokens en vez de max_tokens.</summary>
    public bool UseMaxCompletionTokens { get; set; }

    public int TimeoutSeconds { get; set; } = 60;
    public string AnthropicVersion { get; set; } = "2023-06-01";

    public string? AgentName { get; set; }
    public string FoundryScope { get; set; } = "https://ai.azure.com/.default";
    public bool SendSystemPrompt { get; set; } = true;
}
