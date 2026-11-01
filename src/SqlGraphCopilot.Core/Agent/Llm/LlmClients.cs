using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace SqlGraphCopilot.Core.Agent.Llm;

public enum LlmProvider
{
    /// <summary>Sin LLM: el agente responde con el narrador determinista (funciona sin red).</summary>
    None,

    /// <summary>API compatible con OpenAI Chat Completions: Azure OpenAI, OpenAI, Ollama, LM Studio, vLLM.</summary>
    OpenAICompatible,

    /// <summary>Claude (Anthropic Messages API).</summary>
    Anthropic
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
}

public sealed record LlmPrompt(string System, string User);

public interface ILlmClient
{
    string Name { get; }
    bool IsEnabled { get; }
    Task<string> CompleteAsync(LlmPrompt prompt, CancellationToken cancellationToken);
}

public sealed class DisabledLlmClient : ILlmClient
{
    public string Name => "deterministic";
    public bool IsEnabled => false;
    public Task<string> CompleteAsync(LlmPrompt prompt, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No hay proveedor LLM configurado.");
}

public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    public const string HttpClientName = "SqlGraphCopilot.Llm";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlmOptions _options;

    public OpenAiCompatibleLlmClient(IHttpClientFactory httpClientFactory, IOptions<LlmOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public string Name => $"openai-compatible:{_options.Model ?? "deployment"}";
    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.Endpoint);

    public async Task<string> CompleteAsync(LlmPrompt prompt, CancellationToken cancellationToken)
    {
        JsonObject body = new()
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = prompt.System },
                new JsonObject { ["role"] = "user", ["content"] = prompt.User }
            },
            [_options.UseMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = _options.MaxTokens
        };
        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            body["model"] = _options.Model;
        }
        if (_options.Temperature is double temperature)
        {
            body["temperature"] = temperature;
        }

        using HttpRequestMessage request = new(HttpMethod.Post, _options.Endpoint) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            if (_options.ApiKeyHeader.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(_options.ApiKeyHeader, _options.ApiKey);
            }
        }

        using JsonDocument document = await LlmHttp.SendAsync(_httpClientFactory, request, _options.TimeoutSeconds, cancellationToken);
        return document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }
}

public sealed class AnthropicLlmClient : ILlmClient
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlmOptions _options;

    public AnthropicLlmClient(IHttpClientFactory httpClientFactory, IOptions<LlmOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public string Name => $"anthropic:{_options.Model}";
    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.Model);

    public async Task<string> CompleteAsync(LlmPrompt prompt, CancellationToken cancellationToken)
    {
        JsonObject body = new()
        {
            ["model"] = _options.Model,
            ["max_tokens"] = _options.MaxTokens,
            ["system"] = prompt.System,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = prompt.User }
            }
        };
        if (_options.Temperature is double temperature)
        {
            body["temperature"] = temperature;
        }

        using HttpRequestMessage request = new(HttpMethod.Post, _options.Endpoint ?? DefaultEndpoint) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", _options.AnthropicVersion);

        using JsonDocument document = await LlmHttp.SendAsync(_httpClientFactory, request, _options.TimeoutSeconds, cancellationToken);
        IEnumerable<string> texts = document.RootElement.GetProperty("content").EnumerateArray()
            .Where(block => block.TryGetProperty("type", out JsonElement type) && type.GetString() == "text")
            .Select(block => block.GetProperty("text").GetString() ?? string.Empty);
        return string.Join(Environment.NewLine, texts);
    }
}

internal static class LlmHttp
{
    public static async Task<JsonDocument> SendAsync(IHttpClientFactory factory, HttpRequestMessage request, int timeoutSeconds, CancellationToken cancellationToken)
    {
        HttpClient client = factory.CreateClient(OpenAiCompatibleLlmClient.HttpClientName);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));

        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token);
        string payload = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            // No se registra el cuerpo de la petición (puede contener nombres de objetos internos).
            throw new HttpRequestException($"El proveedor LLM respondió {(int)response.StatusCode}: {Truncate(payload, 300)}");
        }
        return JsonDocument.Parse(payload);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
