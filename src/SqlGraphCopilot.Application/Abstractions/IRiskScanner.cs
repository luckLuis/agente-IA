using SqlGraphCopilot.Application.Mishi;

namespace SqlGraphCopilot.Application.Abstractions;

public interface IRiskScanner
{
    ScanResult Scan(string sql, IReadOnlyList<string> criticalNamePatterns);
}
