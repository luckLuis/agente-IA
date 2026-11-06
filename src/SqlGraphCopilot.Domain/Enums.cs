namespace SqlGraphCopilot.Domain;

public enum NodeKind
{
    Table,
    View,
    StoredProcedure,
    Function,
    Trigger,
    Synonym,

    /// <summary>Objeto de otra base o instancia que todavía no se ha cargado.</summary>
    External,

    /// <summary>Servidor vinculado usado con OPENQUERY (el objeto remoto no es visible en el catálogo).</summary>
    LinkedServer,

    /// <summary>Referencia que SQL Server no pudo resolver: el objeto no existe (referencia rota).</summary>
    Missing
}

public enum ExternalScope
{
    None,
    CrossDatabase,
    LinkedServer
}

/// <summary>
/// Tipo de relación "origen depende de destino". Es [Flags] porque un SP puede leer y escribir la misma tabla.
/// </summary>
[Flags]
public enum DependencyKind
{
    None = 0,

    /// <summary>Referencia detectada por el catálogo, aún sin clasificar (lectura/escritura desconocida).</summary>
    References = 1,
    Reads = 2,
    Writes = 4,
    Executes = 8,
    Calls = 16,

    /// <summary>Trigger → tabla sobre la que se dispara.</summary>
    TriggeredBy = 32,

    /// <summary>Sinónimo → objeto base.</summary>
    SynonymFor = 64
}

public enum TraversalDirection
{
    /// <summary>De qué depende el objeto (lo que usa).</summary>
    Outgoing,

    /// <summary>Quién depende del objeto (quién lo usa).</summary>
    Incoming,
    Both
}
