using Microsoft.Extensions.Options;

namespace SqlGraphCopilot.Core.Metadata;

public interface IInstanceRegistry
{
    IReadOnlyList<SqlInstanceOptions> Instances { get; }
    bool TryGet(string name, out SqlInstanceOptions? instance);
    SqlInstanceOptions GetRequired(string name);

    /// <summary>Traduce un nombre de servidor (instancia, @@SERVERNAME o linked server) al nombre registrado. Null si no está registrado.</summary>
    string? ResolveServer(string serverName);

    bool IsDatabaseAllowed(string instanceName, string database);
}

public sealed class InstanceRegistry : IInstanceRegistry
{
    private readonly Dictionary<string, SqlInstanceOptions> _byName;
    private readonly Dictionary<string, string> _aliases;

    public InstanceRegistry(IOptions<SqlGraphCopilotOptions> options)
    {
        Instances = options.Value.Instances;
        _byName = new Dictionary<string, SqlInstanceOptions>(StringComparer.OrdinalIgnoreCase);
        _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (SqlInstanceOptions instance in Instances)
        {
            _byName[instance.Name] = instance;
            _aliases[instance.Name] = instance.Name;
            foreach (string alias in instance.LinkedServerAliases.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                _aliases[alias.Trim().Trim('[', ']')] = instance.Name;
            }
        }
    }

    public IReadOnlyList<SqlInstanceOptions> Instances { get; }

    public bool TryGet(string name, out SqlInstanceOptions? instance) => _byName.TryGetValue(name, out instance);

    public SqlInstanceOptions GetRequired(string name) =>
        _byName.TryGetValue(name, out SqlInstanceOptions? instance) ? instance : throw new InstanceNotFoundException(name);

    public string? ResolveServer(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return null;
        }
        return _aliases.TryGetValue(serverName.Trim().Trim('[', ']'), out string? resolved) ? resolved : null;
    }

    public bool IsDatabaseAllowed(string instanceName, string database)
    {
        SqlInstanceOptions instance = GetRequired(instanceName);
        if (instance.ExcludedDatabases.Contains(database, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        return instance.AllowedDatabases.Count == 0 ||
               instance.AllowedDatabases.Contains(database, StringComparer.OrdinalIgnoreCase);
    }
}
