using SqlGraphCopilot.Application.Settings;

namespace SqlGraphCopilot.Application.Abstractions;

public interface IInstanceRegistry
{
    IReadOnlyList<SqlInstanceOptions> Instances { get; }
    bool TryGet(string name, out SqlInstanceOptions? instance);
    SqlInstanceOptions GetRequired(string name);

    /// <summary>Traduce un nombre de servidor (instancia, @@SERVERNAME o linked server) al nombre registrado. Null si no está registrado.</summary>
    string? ResolveServer(string serverName);

    bool IsDatabaseAllowed(string instanceName, string database);
}
