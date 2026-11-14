using SqlGraphCopilot.Application.Migration;

namespace SqlGraphCopilot.Application.Abstractions;

/// <summary>Compila y ejecuta el código C# migrado y sus pruebas xUnit (solo desarrollo).</summary>
public interface IMigrationVerifier
{
    Task<VerifyReport> VerifyAsync(IReadOnlyList<VerifyFile> files, CancellationToken cancellationToken);
}
