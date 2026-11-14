namespace SqlGraphCopilot.Application.Migration;

public sealed class MigrationVerifierOptions
{
    /// <summary>Apagado por defecto: compilar y ejecutar código generado es ejecución de código en el servidor.</summary>
    public bool Enabled { get; set; }

    public string TargetFramework { get; set; } = "net8.0";
    public int TimeoutSeconds { get; set; } = 240;

    /// <summary>Opcional. Si existe, se pasa como SQLGRAPH_TEST_CONN para que corran las pruebas de paridad contra el SP real.</summary>
    public string? ParityConnectionString { get; set; }
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
