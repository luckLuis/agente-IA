using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Catalog;
using SqlGraphCopilot.Application.Settings;
using SqlGraphCopilot.Domain.Exceptions;

namespace SqlGraphCopilot.Infrastructure.SqlServer;

public sealed class SqlServerCatalogReader : ICatalogReader
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly SqlGraphCopilotOptions _options;
    private readonly ILogger<SqlServerCatalogReader> _logger;

    public SqlServerCatalogReader(
        IDbConnectionFactory connectionFactory,
        IOptions<SqlGraphCopilotOptions> options,
        ILogger<SqlServerCatalogReader> logger)
    {
        _connectionFactory = connectionFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(string instance, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await OpenAsync(instance, null, cancellationToken);
        await using DbCommand command = CreateCommand(connection, SqlCatalogQueries.ListDatabases);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        List<DatabaseInfo> databases = new();
        while (await reader.ReadAsync(cancellationToken))
        {
            databases.Add(new DatabaseInfo(reader.GetString(0), Convert.ToInt32(reader.GetValue(1))));
        }
        return databases;
    }

    public async Task<CatalogSnapshot> ReadCatalogAsync(string instance, string database, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await OpenAsync(instance, database, cancellationToken);

        string objectsQuery = _options.ComputeDefinitionHash ? SqlCatalogQueries.ObjectsWithHash : SqlCatalogQueries.ObjectsWithoutHash;

        List<CatalogObject> objects = await ReadListAsync(connection, objectsQuery, r => new CatalogObject(
            ObjectId: r.GetInt32(0),
            Schema: r.GetString(1),
            Name: r.GetString(2),
            TypeCode: r.GetString(3),
            ModifiedAt: r.GetDateTime(4),
            HasDynamicSqlHint: r.GetBoolean(5),
            UsesOpenQueryHint: r.GetBoolean(6),
            DefinitionHash: r.IsDBNull(7) ? null : r.GetString(7).Trim()), cancellationToken);

        List<CatalogDependency> dependencies = await ReadListAsync(connection, SqlCatalogQueries.Dependencies, r => new CatalogDependency(
            ReferencingId: r.GetInt32(0),
            ReferencedServer: NullableString(r, 1),
            ReferencedDatabase: NullableString(r, 2),
            ReferencedSchema: NullableString(r, 3),
            ReferencedName: r.GetString(4),
            ReferencedId: r.IsDBNull(5) ? null : r.GetInt32(5),
            IsCallerDependent: r.GetBoolean(6),
            IsAmbiguous: r.GetBoolean(7)), cancellationToken);

        List<CatalogSynonym> synonyms = await ReadListAsync(connection, SqlCatalogQueries.Synonyms, r => new CatalogSynonym(
            ObjectId: r.GetInt32(0),
            BaseServer: NullableString(r, 1),
            BaseDatabase: NullableString(r, 2),
            BaseSchema: NullableString(r, 3),
            BaseName: r.IsDBNull(4) ? string.Empty : r.GetString(4)), cancellationToken);

        List<CatalogTrigger> triggers = await ReadListAsync(connection, SqlCatalogQueries.Triggers, r => new CatalogTrigger(
            TriggerId: r.GetInt32(0),
            ParentId: r.GetInt32(1)), cancellationToken);

        _logger.LogInformation(
            "Catálogo leído {Instance}/{Database}: {Objects} objetos, {Dependencies} dependencias, {Synonyms} sinónimos, {Triggers} triggers",
            instance, database, objects.Count, dependencies.Count, synonyms.Count, triggers.Count);

        return new CatalogSnapshot(instance, database, objects, dependencies, synonyms.Where(s => s.BaseName.Length > 0).ToList(), triggers);
    }

    public async Task<string?> ReadDefinitionAsync(string instance, string database, int objectId, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await OpenAsync(instance, database, cancellationToken);
        await using DbCommand command = CreateCommand(connection, SqlCatalogQueries.Definition);
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@objectId";
        parameter.DbType = DbType.Int32;
        parameter.Value = objectId;
        command.Parameters.Add(parameter);

        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : (string)value;
    }

    public async IAsyncEnumerable<ObjectDefinition> ReadDefinitionsAsync(
        string instance,
        string database,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using DbConnection connection = await OpenAsync(instance, database, cancellationToken);
        await using DbCommand command = CreateCommand(connection, SqlCatalogQueries.AllDefinitions);
        command.CommandTimeout = Math.Max(_options.CommandTimeoutSeconds, 300);

        // SequentialAccess: no carga todas las definiciones en memoria a la vez.
        await using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            int objectId = reader.GetInt32(0);
            string definition = reader.GetString(1);
            yield return new ObjectDefinition(objectId, definition);
        }
    }

    private async Task<DbConnection> OpenAsync(string instance, string? database, CancellationToken cancellationToken)
    {
        DbConnection connection = _connectionFactory.Create(instance, database);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (DbException ex)
        {
            await connection.DisposeAsync();
            if (database is not null)
            {
                throw new DatabaseNotAccessibleException(instance, database, ex.Message, ex);
            }
            throw new DataSourceUnavailableException(instance, ex);
        }
    }

    private DbCommand CreateCommand(DbConnection connection, string sql)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        return command;
    }

    private async Task<List<T>> ReadListAsync<T>(DbConnection connection, string sql, Func<DbDataReader, T> map, CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(connection, sql);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<T> items = new();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(map(reader));
        }
        return items;
    }

    private static string? NullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
