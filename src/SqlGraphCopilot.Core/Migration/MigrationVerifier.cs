using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace SqlGraphCopilot.Core.Migration;

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

/// <summary>
/// Compila y ejecuta (dotnet test) el código C# y las pruebas xUnit que generó el copiloto, en un proyecto temporal.
/// Solo para desarrollo: el filtro de patrones peligrosos NO es un sandbox. Para producción, ejecútelo en un contenedor aislado.
/// </summary>
public sealed class MigrationVerifier
{
    private const int MaxFiles = 20;
    private const int MaxTotalChars = 200_000;
    private static readonly Regex FileName = new(@"^[A-Za-z0-9_.\-]{1,80}\.cs$", RegexOptions.Compiled);
    private static readonly Regex Forbidden = new(
        @"System\.Diagnostics\.Process|\bProcess\.Start|System\.Net\.|\bHttpClient\b|System\.IO\.File|\bFile\.|\bDirectory\.|\bRegistry\b|DllImport|Assembly\.Load|Environment\.Exit|Reflection\.Emit|\bunsafe\b",
        RegexOptions.Compiled);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly MigrationVerifierOptions _options;

    public MigrationVerifier(IOptions<MigrationVerifierOptions> options)
    {
        _options = options.Value;
    }

    public async Task<VerifyReport> VerifyAsync(IReadOnlyList<VerifyFile> files, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("La validación de código está desactivada. Actívela con MigrationVerifier:Enabled=true (solo en desarrollo).");
        }
        Validate(files);

        string root = Path.Combine(Path.GetTempPath(), "sqlgraphcopilot-verify", Guid.NewGuid().ToString("N"));
        await Gate.WaitAsync(cancellationToken);   // un proyecto a la vez: dotnet test es pesado
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "Verify.csproj"), Csproj(_options.TargetFramework), cancellationToken);
            foreach (VerifyFile file in files)
            {
                await File.WriteAllTextAsync(Path.Combine(root, file.Name), file.Content, cancellationToken);
            }

            Dictionary<string, string> env = new()
            {
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "1"
            };
            List<string> notes = new();
            if (!string.IsNullOrWhiteSpace(_options.ParityConnectionString))
            {
                env["SQLGRAPH_TEST_CONN"] = _options.ParityConnectionString;
                notes.Add("Las pruebas de paridad se ejecutaron contra el SP real (dentro de transacciones con ROLLBACK).");
            }
            else
            {
                notes.Add("Las pruebas de paridad se omitieron: no hay cadena de conexión configurada (MigrationVerifier:ParityConnectionString).");
            }

            (int exitCode, string output, bool timedOut) = await RunAsync(
                root,
                ["test", "Verify.csproj", "--nologo", "--logger", "trx;LogFileName=results.trx", "--results-directory", root, "-v", "m"],
                TimeSpan.FromSeconds(_options.TimeoutSeconds),
                env,
                cancellationToken);

            string trx = Path.Combine(root, "results.trx");
            List<string> buildErrors = ParseBuildErrors(output, root);
            if (!File.Exists(trx))
            {
                if (timedOut) notes.Add($"Se canceló por superar {_options.TimeoutSeconds} s.");
                if (buildErrors.Count == 0 && !timedOut) buildErrors.Add(Tail(output, 1500));
                return new VerifyReport(false, 0, 0, 0, [], buildErrors, notes, timedOut);
            }

            List<VerifyTest> tests = ParseTrx(trx);
            return new VerifyReport(
                Compiled: true,
                Passed: tests.Count(t => t.Outcome == "Passed"),
                Failed: tests.Count(t => t.Outcome == "Failed"),
                Skipped: tests.Count(t => t.Outcome is not ("Passed" or "Failed")),
                Tests: tests,
                BuildErrors: [],
                Notes: notes,
                TimedOut: timedOut);
        }
        finally
        {
            Gate.Release();
            try { Directory.Delete(root, recursive: true); } catch { /* se limpia en la próxima limpieza del temporal */ }
        }
    }

    private static void Validate(IReadOnlyList<VerifyFile> files)
    {
        if (files.Count == 0) throw new ArgumentException("No hay código C# para validar.");
        if (files.Count > MaxFiles) throw new ArgumentException($"Demasiados archivos (máximo {MaxFiles}).");
        if (files.Sum(f => f.Content.Length) > MaxTotalChars) throw new ArgumentException("El código es demasiado grande.");
        if (files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new ArgumentException("Hay nombres de archivo repetidos.");

        foreach (VerifyFile file in files)
        {
            if (!FileName.IsMatch(file.Name)) throw new ArgumentException($"Nombre de archivo no válido: {file.Name}");
            Match bad = Forbidden.Match(file.Content);
            if (bad.Success) throw new ArgumentException($"El código usa algo no permitido en la validación ({bad.Value.Trim()}) en {file.Name}.");
        }
    }

    private static string Csproj(string framework) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>{framework}</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <IsPackable>false</IsPackable>
            <IsTestProject>true</IsTestProject>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
            <PackageReference Include="xunit" Version="2.9.2" />
            <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
            <PackageReference Include="FluentAssertions" Version="6.12.1" />
            <PackageReference Include="Moq" Version="4.20.72" />
            <PackageReference Include="Microsoft.Data.SqlClient" Version="5.2.2" />
          </ItemGroup>
        </Project>
        """;

    private static List<VerifyTest> ParseTrx(string path)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        XDocument document = XDocument.Load(path);
        return document.Descendants(ns + "UnitTestResult").Select(result =>
        {
            string outcome = (string?)result.Attribute("outcome") ?? "Unknown";
            string? message = result.Descendants(ns + "Message").FirstOrDefault()?.Value;
            return new VerifyTest((string?)result.Attribute("testName") ?? "?", outcome, message is null ? null : Tail(message, 600));
        }).OrderBy(t => t.Outcome == "Failed" ? 0 : 1).ThenBy(t => t.Name).ToList();
    }

    private static List<string> ParseBuildErrors(string output, string root) =>
        output.Split('\n')
            .Where(line => line.Contains(": error ", StringComparison.Ordinal))
            .Select(line => line.Replace(root + Path.DirectorySeparatorChar, string.Empty).Trim())
            .Select(line => Regex.Replace(line, @"\s*\[[^\]]*Verify\.csproj\]$", string.Empty))
            .Distinct()
            .Take(15)
            .ToList();

    private static string Tail(string text, int max) => text.Length <= max ? text : "…" + text[^max..];

    private static async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        IDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        foreach ((string key, string value) in environment) start.Environment[key] = value;

        using Process process = new() { StartInfo = start };
        StringBuilder output = new();
        object sync = new();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sync) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sync) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            process.WaitForExit();   // vacía las salidas redirigidas
            return (process.ExitCode, output.ToString(), false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
            if (cancellationToken.IsCancellationRequested) throw;
            return (-1, output.ToString(), true);
        }
    }
}
