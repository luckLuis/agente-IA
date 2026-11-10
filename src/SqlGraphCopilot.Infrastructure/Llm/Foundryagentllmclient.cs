using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Options;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Usage;

namespace SqlGraphCopilot.Infrastructure.Llm;

public sealed class FoundryAgentLlmClient : ILlmClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlmOptions _options;
    private readonly TokenCredential _credential;

    public FoundryAgentLlmClient(IHttpClientFactory httpClientFactory, IOptions<LlmOptions> options, TokenCredential credential)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _credential = credential;
    }

    public string Name => !string.IsNullOrWhiteSpace(_options.AgentName)
        ? $"foundry-agent:{_options.AgentName}"
        : $"foundry:{_options.Model}";

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(_options.Endpoint)
        && (!string.IsNullOrWhiteSpace(_options.AgentName) || !string.IsNullOrWhiteSpace(_options.Model));

    public async Task<string> CompleteAsync(LlmPrompt prompt, CancellationToken cancellationToken)
    {
        string input = _options.SendSystemPrompt && !string.IsNullOrWhiteSpace(prompt.System)
            ? new StringBuilder()
                .AppendLine("[REGLAS]").AppendLine(prompt.System).AppendLine()
                .AppendLine("[CONSULTA]").Append(prompt.User).ToString()
            : prompt.User;

        JsonObject body = new()
        {
            ["input"] = input,
            ["max_output_tokens"] = _options.MaxTokens
        };
        if (!string.IsNullOrWhiteSpace(_options.AgentName))
        {
            body["agent"] = new JsonObject { ["type"] = "agent_reference", ["name"] = _options.AgentName };
        }
        else
        {
            body["model"] = _options.Model;
        }
        if (_options.Temperature is double temperature)
        {
            body["temperature"] = temperature;
        }

        string url = $"{_options.Endpoint!.TrimEnd('/')}/openai/v1/responses";
        AccessToken token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { _options.FoundryScope }), cancellationToken);

        using HttpRequestMessage request = new(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using JsonDocument document = await LlmHttp.SendAsync(_httpClientFactory, request, _options.TimeoutSeconds, cancellationToken);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("usage", out JsonElement usage)
            && usage.TryGetProperty("input_tokens", out JsonElement inputTokens)
            && usage.TryGetProperty("output_tokens", out JsonElement outputTokens))
        {
            UsageContext.Current?.ReportProviderUsage(inputTokens.GetInt32(), outputTokens.GetInt32());
        }

        return ExtractText(root);
    }

    private static string ExtractText(JsonElement root)
    {
        if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
        {
            string message = error.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "error desconocido" : "error desconocido";
            throw new HttpRequestException($"El agente de Foundry devolvió un error: {message}");
        }

        List<string> texts = new();
        if (root.TryGetProperty("output", out JsonElement output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out JsonElement itemType) || itemType.GetString() != "message"
                    || !item.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (JsonElement block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out JsonElement blockType) && blockType.GetString() == "output_text"
                        && block.TryGetProperty("text", out JsonElement text))
                    {
                        texts.Add(text.GetString() ?? string.Empty);
                    }
                }
            }
        }
        return string.Join(Environment.NewLine, texts);
    }
}
