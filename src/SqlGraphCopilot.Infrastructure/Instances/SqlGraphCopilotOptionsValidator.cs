using Microsoft.Extensions.Options;
using SqlGraphCopilot.Application.Settings;

namespace SqlGraphCopilot.Infrastructure.Instances;

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
