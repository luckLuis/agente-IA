using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Catalog;

namespace SqlGraphCopilot.Tests.Fakes;

/// <summary>
/// Catálogo en memoria SOLO para pruebas: reproduce lo que devolverían sys.objects,
/// sys.sql_expression_dependencies, sys.synonyms y sys.triggers.
/// </summary>
internal sealed class FakeCatalog : ICatalogReader
{
    private readonly Dictionary<string, FakeDatabase> _databases = new(StringComparer.OrdinalIgnoreCase);

    public int CatalogReads { get; private set; }

    public FakeDatabase Database(string instance, string database)
    {
        string key = $"{instance}|{database}";
        if (!_databases.TryGetValue(key, out FakeDatabase? db))
        {
            db = new FakeDatabase(instance, database);
            _databases[key] = db;
        }
        return db;
    }

    public Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(string instance, CancellationToken cancellationToken)
    {
        IReadOnlyList<DatabaseInfo> list = _databases.Values
            .Where(d => d.Instance.Equals(instance, StringComparison.OrdinalIgnoreCase))
            .Select(d => new DatabaseInfo(d.Name, 160))
            .ToList();
        return Task.FromResult(list);
    }

    public Task<CatalogSnapshot> ReadCatalogAsync(string instance, string database, CancellationToken cancellationToken)
    {
        CatalogReads++;
        FakeDatabase db = Database(instance, database);
        return Task.FromResult(new CatalogSnapshot(instance, db.Name, db.Objects.ToList(), db.Dependencies.ToList(), db.Synonyms.ToList(), db.Triggers.ToList()));
    }

    public Task<string?> ReadDefinitionAsync(string instance, string database, int objectId, CancellationToken cancellationToken) =>
        Task.FromResult(Database(instance, database).Definitions.TryGetValue(objectId, out string? definition) ? definition : null);

    public async IAsyncEnumerable<ObjectDefinition> ReadDefinitionsAsync(string instance, string database, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (KeyValuePair<int, string> pair in Database(instance, database).Definitions)
        {
            await Task.Yield();
            yield return new ObjectDefinition(pair.Key, pair.Value);
        }
    }
}

internal sealed class FakeDatabase
{
    private int _nextId = 100;

    public FakeDatabase(string instance, string name)
    {
        Instance = instance;
        Name = name;
    }

    public string Instance { get; }
    public string Name { get; }
    public List<CatalogObject> Objects { get; } = new();
    public List<CatalogDependency> Dependencies { get; } = new();
    public List<CatalogSynonym> Synonyms { get; } = new();
    public List<CatalogTrigger> Triggers { get; } = new();
    public Dictionary<int, string> Definitions { get; } = new();

    public int Table(string name, string schema = "dbo") => Add(schema, name, "U", null);
    public int View(string name, string definition, string schema = "dbo") => Add(schema, name, "V", definition);
    public int Proc(string name, string? definition = null, string schema = "dbo") => Add(schema, name, "P", definition ?? $"CREATE PROCEDURE {schema}.{name} AS SELECT 1;");
    public int Function(string name, string schema = "dbo") => Add(schema, name, "FN", $"CREATE FUNCTION {schema}.{name}() RETURNS INT AS BEGIN RETURN 1 END");
    public int Trigger(string name, int tableId, string definition, string schema = "dbo")
    {
        int id = Add(schema, name, "TR", definition);
        Triggers.Add(new CatalogTrigger(id, tableId));
        return id;
    }

    public int Synonym(string name, string? server, string? database, string? schema, string baseName, string synonymSchema = "dbo")
    {
        int id = Add(synonymSchema, name, "SN", null);
        Synonyms.Add(new CatalogSynonym(id, server, database, schema, baseName));
        return id;
    }

    /// <summary>Referencia resuelta por SQL Server dentro de la misma base.</summary>
    public FakeDatabase Ref(int fromId, int toId)
    {
        CatalogObject target = Objects.Single(o => o.ObjectId == toId);
        Dependencies.Add(new CatalogDependency(fromId, null, null, target.Schema, target.Name, toId, false, false));
        return this;
    }

    /// <summary>Referencia de 3 o 4 partes (otra base o servidor vinculado): SQL Server no la resuelve.</summary>
    public FakeDatabase RefExternal(int fromId, string? server, string database, string schema, string name)
    {
        Dependencies.Add(new CatalogDependency(fromId, server, database, schema, name, null, false, false));
        return this;
    }

    /// <summary>Referencia no resuelta (sin esquema o a un objeto inexistente).</summary>
    public FakeDatabase RefUnresolved(int fromId, string? schema, string name)
    {
        Dependencies.Add(new CatalogDependency(fromId, null, null, schema, name, null, schema is null, false));
        return this;
    }

    private int Add(string schema, string name, string type, string? definition)
    {
        int id = _nextId++;
        string? hash = definition is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(definition)));
        bool dynamic = definition is not null && (definition.Contains("sp_executesql", StringComparison.OrdinalIgnoreCase) || definition.Contains("EXEC (", StringComparison.OrdinalIgnoreCase) || definition.Contains("EXEC(", StringComparison.OrdinalIgnoreCase));
        bool openQuery = definition is not null && definition.Contains("OPENQUERY", StringComparison.OrdinalIgnoreCase);
        Objects.Add(new CatalogObject(id, schema, name, type, new DateTime(2026, 1, 1), dynamic, openQuery, hash));
        if (definition is not null)
        {
            Definitions[id] = definition;
        }
        return id;
    }
}
