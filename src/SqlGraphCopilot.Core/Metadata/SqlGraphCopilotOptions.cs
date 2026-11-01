using Microsoft.Extensions.Options;

namespace SqlGraphCopilot.Core.Metadata;

public sealed class SqlGraphCopilotOptions
{
    public const string SectionName = "SqlGraphCopilot";

    /// <summary>Instancias que el usuario puede seleccionar. Nada se consulta fuera de esta lista.</summary>
    public List<SqlInstanceOptions> Instances { get; set; } = new();

    /// <summary>Expresiones regulares (sin distinción de mayúsculas) que marcan objetos como críticos. Ej.: "Cierre", "^dbo\.Clientes$".</summary>
    public List<string> CriticalNamePatterns { get; set; } = new();

    /// <summary>Minutos que se conserva en memoria el índice de una base antes de releer el catálogo.</summary>
    public int IndexCacheMinutes { get; set; } = 30;

    public int CommandTimeoutSeconds { get; set; } = 60;

    public int DefaultNeighborhoodDepth { get; set; } = 1;
    public int MaxNeighborhoodDepth { get; set; } = 4;

    /// <summary>Tope de nodos por respuesta: evita saturar el navegador y el servidor.</summary>
    public int MaxNodesPerRequest { get; set; } = 250;

    public int DefaultHubCount { get; set; } = 30;

    /// <summary>Calcula SHA-256 de cada definición (detecta SPs duplicados). Requiere SQL Server 2016+ para definiciones de más de 8000 bytes.</summary>
    public bool ComputeDefinitionHash { get; set; } = true;

    /// <summary>Al expandir un nodo, clasifica (lee/escribe/ejecuta) ese nodo y sus vecinos programables.</summary>
    public bool AutoClassifyOnExpand { get; set; } = true;

    public int MaxAutoClassifyPerRequest { get; set; } = 25;
}

public sealed class SqlInstanceOptions
{
    /// <summary>Nombre lógico (único) que se muestra al usuario y forma parte del Id de los objetos.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Cadena de conexión con un login de SOLO LECTURA de metadata (VIEW DEFINITION). Use user-secrets o variables de entorno.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public string Environment { get; set; } = "Desarrollo";
    public string? Description { get; set; }

    /// <summary>
    /// Nombres con que OTRAS bases se refieren a esta instancia: @@SERVERNAME y nombres de linked server.
    /// Permite unir el puente inter-instancia con el índice real de esta instancia.
    /// </summary>
    public List<string> LinkedServerAliases { get; set; } = new();

    /// <summary>Si tiene elementos, solo estas bases se pueden seleccionar.</summary>
    public List<string> AllowedDatabases { get; set; } = new();

    public List<string> ExcludedDatabases { get; set; } = new();
}

internal sealed class SqlGraphCopilotOptionsValidator : IValidateOptions<SqlGraphCopilotOptions>
{
    public ValidateOptionsResult Validate(string? name, SqlGraphCopilotOptions options)
    {
        List<string> failures = new();
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (SqlInstanceOptions instance in options.Instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Name))
            {
                failures.Add("Cada instancia debe tener Name.");
                continue;
            }
            if (instance.Name.Contains('|'))
            {
                failures.Add($"El nombre de instancia '{instance.Name}' no puede contener '|'.");
            }
            if (!names.Add(instance.Name))
            {
                failures.Add($"Instancia duplicada: '{instance.Name}'.");
            }
            if (string.IsNullOrWhiteSpace(instance.ConnectionString))
            {
                failures.Add($"La instancia '{instance.Name}' no tiene ConnectionString.");
            }
        }

        if (options.MaxNodesPerRequest is < 10 or > 2000)
        {
            failures.Add("MaxNodesPerRequest debe estar entre 10 y 2000.");
        }
        if (options.MaxNeighborhoodDepth is < 1 or > 6)
        {
            failures.Add("MaxNeighborhoodDepth debe estar entre 1 y 6.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
