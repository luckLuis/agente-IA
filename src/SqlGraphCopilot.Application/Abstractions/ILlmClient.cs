namespace SqlGraphCopilot.Application.Abstractions;

public sealed record LlmPrompt(string System, string User);

public interface ILlmClient
{
    string Name { get; }
    bool IsEnabled { get; }
    Task<string> CompleteAsync(LlmPrompt prompt, CancellationToken cancellationToken);
}
