namespace SqlGraphCopilot.Core.Metadata;

public abstract class SqlGraphException : Exception
{
    protected SqlGraphException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class InstanceNotFoundException : SqlGraphException
{
    public InstanceNotFoundException(string instance)
        : base($"La instancia '{instance}' no está registrada en la configuración (SqlGraphCopilot:Instances).") { }
}

public sealed class DatabaseNotAccessibleException : SqlGraphException
{
    public DatabaseNotAccessibleException(string instance, string database, string? reason = null, Exception? inner = null)
        : base($"No se puede acceder a la base '{database}' en '{instance}'.{(reason is null ? string.Empty : " " + reason)}", inner) { }
}

public sealed class DataSourceUnavailableException : SqlGraphException
{
    public DataSourceUnavailableException(string instance, Exception inner)
        : base($"No se pudo conectar a la instancia '{instance}': {inner.Message}", inner) { }
}

public sealed class ObjectNotFoundException : SqlGraphException
{
    public ObjectNotFoundException(string objectId)
        : base($"El objeto '{objectId}' no existe en el índice.") { }
}
