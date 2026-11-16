namespace SqlGraphCopilot.Application.Migration;

public sealed class MigrationVerifierOptions
{
    /// <summary>Apagado por defecto: compilar y ejecutar código generado es ejecución de código en el servidor.</summary>
    public bool Enabled { get; set; }

    public string TargetFramework { get; set; } = "net8.0";
    public int TimeoutSeconds { get; set; } = 240;
}

public sealed record VerifyFile(string Name, string Content);
public sealed record VerifyTest(string Name, string Outcome, string? Message);

public sealed record VerifyReport(
    bool Compiled,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<VerifyTest> Tests,
    IReadOnlyList<string> BuildErrors,
    IReadOnlyList<string> Notes,
    bool TimedOut);
