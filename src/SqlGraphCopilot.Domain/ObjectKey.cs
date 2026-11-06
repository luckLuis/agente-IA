namespace SqlGraphCopilot.Domain;

/// <summary>
/// Identidad canónica de un objeto SQL en 4 partes: instancia | base | esquema | nombre.
/// La parte "instancia" es el nombre REGISTRADO en configuración (no @@SERVERNAME),
/// así el mismo objeto tiene el mismo Id desde cualquier base que lo referencie.
/// </summary>
public sealed class ObjectKey : IEquatable<ObjectKey>
{
    public const char Separator = '|';
    public const string DefaultSchema = "dbo";

    public ObjectKey(string server, string database, string? schema, string name)
    {
        Server = Clean(server, nameof(server));
        Database = Clean(database, nameof(database));
        Schema = string.IsNullOrWhiteSpace(schema) ? DefaultSchema : Clean(schema, nameof(schema));
        Name = Clean(name, nameof(name));
        Id = string.Join(Separator, Server, Database, Schema, Name).ToUpperInvariant();
    }

    public string Server { get; }
    public string Database { get; }
    public string Schema { get; }
    public string Name { get; }

    /// <summary>Id estable, sin distinción de mayúsculas (se usa como Id de nodo en API y frontend).</summary>
    public string Id { get; }

    public string DisplayName => $"{Schema}.{Name}";
    public string FullName => $"[{Server}].[{Database}].[{Schema}].[{Name}]";

    public bool IsInDatabase(string server, string database) =>
        string.Equals(Server, server, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Database, database, StringComparison.OrdinalIgnoreCase);

    public static ObjectKey Parse(string id)
    {
        if (!TryParse(id, out ObjectKey? key))
        {
            throw new FormatException($"Id de objeto inválido: '{id}'. Formato esperado: instancia|base|esquema|nombre.");
        }
        return key!;
    }

    public static bool TryParse(string? id, out ObjectKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        string[] parts = id.Split(Separator);
        if (parts.Length != 4 || parts.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        key = new ObjectKey(parts[0], parts[1], parts[2], parts[3]);
        return true;
    }

    public bool Equals(ObjectKey? other) => other is not null && string.Equals(Id, other.Id, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is ObjectKey other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);
    public override string ToString() => FullName;

    private static string Clean(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("El valor no puede estar vacío.", paramName);
        }

        string trimmed = value.Trim().Trim('[', ']', '"');
        if (trimmed.Contains(Separator))
        {
            throw new ArgumentException($"El nombre no puede contener '{Separator}'.", paramName);
        }
        return trimmed;
    }
}
