using System.Data;
using DbTreeView.Web.Models;
using Microsoft.Data.SqlClient;

namespace DbTreeView.Web.Services;

public sealed class SqlServerMetadataService(DatabaseSession session, ILogger<SqlServerMetadataService> logger)
{
    private const string DatabasesQuery = """
        SELECT name
        FROM sys.databases
        WHERE database_id > 4
          AND state = 0
          AND source_database_id IS NULL
          AND HAS_DBACCESS(name) = 1
        ORDER BY name;
        """;

    private const string ObjectsQuery = """
        SELECT
            o.object_id,
            s.name AS schema_name,
            o.name AS object_name,
            o.type
        FROM sys.objects AS o
        INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        WHERE o.is_ms_shipped = 0
          AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF')
        ORDER BY s.name, o.name;
        """;

    private const string DependenciesQuery = """
        SELECT DISTINCT
            d.referencing_id,
            d.referenced_id,
            d.referenced_server_name,
            d.referenced_database_name,
            d.referenced_schema_name,
            d.referenced_entity_name
        FROM sys.sql_expression_dependencies AS d
        WHERE d.referencing_id IS NOT NULL
          AND d.referenced_entity_name IS NOT NULL;
        """;

    private const string ForeignKeysQuery = """
        SELECT
            fk.object_id,
            fk.parent_object_id,
            fk.referenced_object_id,
            fk.name
        FROM sys.foreign_keys AS fk
        WHERE fk.is_ms_shipped = 0;
        """;

    public async Task<ConnectionTestResult> TestConnectionAsync(
        DatabaseConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!options.UseIntegratedSecurity &&
            (string.IsNullOrWhiteSpace(options.Username) || string.IsNullOrEmpty(options.Password)))
        {
            return new ConnectionTestResult(false, "Le nom d’utilisateur et le mot de passe sont obligatoires pour l’authentification SQL Server.");
        }

        if (!options.AnalyzeEntireServer && string.IsNullOrWhiteSpace(options.Database))
        {
            return new ConnectionTestResult(false, "Le nom de la base est obligatoire en mode base unique.");
        }

        try
        {
            var connectionString = options.BuildConnectionString();
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            var serverName = await ReadServerNameAsync(connection, cancellationToken) ?? options.Server.Trim();

            IReadOnlyList<string> databases;
            string databaseName;
            if (options.AnalyzeEntireServer)
            {
                databases = await ReadAccessibleDatabasesAsync(connection, cancellationToken);
                databaseName = string.Empty;
                if (databases.Count == 0)
                {
                    session.Clear();
                    return new ConnectionTestResult(
                        false,
                        "Connexion établie, mais aucune base utilisateur en ligne et accessible n’a été trouvée.");
                }
            }
            else
            {
                databaseName = connection.Database;
                databases = [databaseName];
            }

            session.Connect(
                connectionString,
                serverName,
                databaseName,
                connection.ServerVersion,
                options.AnalyzeEntireServer,
                databases);

            return new ConnectionTestResult(
                true,
                options.AnalyzeEntireServer
                    ? $"Connexion établie : {databases.Count} base(s) utilisateur accessible(s)."
                    : $"Connexion établie à {databaseName}.",
                connection.ServerVersion,
                databaseName,
                databases.Count);
        }
        catch (SqlException exception)
        {
            logger.LogWarning(exception, "SQL Server connection failed for {Server}/{Database}", options.Server, options.Database);
            session.Clear();
            return new ConnectionTestResult(false, ToFriendlyConnectionMessage(exception));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Invalid SQL Server connection configuration");
            session.Clear();
            return new ConnectionTestResult(false, $"Configuration de connexion invalide : {exception.Message}");
        }
    }

    public async Task<DatabaseMap> ReadMapAsync(CancellationToken cancellationToken = default)
    {
        if (!session.IsConnected || session.Databases.Count == 0)
        {
            throw new InvalidOperationException("Aucune connexion SQL Server active.");
        }

        var nodes = new Dictionary<string, DatabaseMapNode>(StringComparer.OrdinalIgnoreCase);
        var objectsByKey = new Dictionary<string, DatabaseMapNode>(StringComparer.OrdinalIgnoreCase);
        var objectsByQualifiedName = new Dictionary<string, DatabaseMapNode>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new List<PendingDependency>();
        var edges = new List<DatabaseMapEdge>();
        var warnings = new List<string>();
        var scannedDatabases = new List<string>();
        var emptyDatabaseCount = 0;

        foreach (var database in session.Databases)
        {
            try
            {
                await using var connection = new SqlConnection(session.GetConnectionString(database));
                await connection.OpenAsync(cancellationToken);
                scannedDatabases.Add(database);

                var databaseNode = new DatabaseMapNode(
                    DatabaseId(database),
                    database,
                    database,
                    string.Empty,
                    DatabaseObjectKind.Database,
                    Database: database);
                nodes[databaseNode.Id] = databaseNode;

                var databaseObjects = await ReadObjectsAsync(
                    connection,
                    database,
                    session.IsServerScope,
                    cancellationToken);

                if (databaseObjects.Count == 0)
                {
                    emptyDatabaseCount++;
                }

                foreach (var schema in databaseObjects.Values
                    .Select(node => node.Schema)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var schemaNode = new DatabaseMapNode(
                        SchemaId(database, schema),
                        schema,
                        session.IsServerScope ? $"{database}.{schema}" : schema,
                        schema,
                        DatabaseObjectKind.Schema,
                        databaseNode.Id,
                        Database: database);
                    nodes.TryAdd(schemaNode.Id, schemaNode);
                }

                foreach (var pair in databaseObjects)
                {
                    nodes[pair.Value.Id] = pair.Value;
                    objectsByKey[ObjectKey(database, pair.Key)] = pair.Value;
                    objectsByQualifiedName[QualifiedKey(database, pair.Value.Schema, pair.Value.Name)] = pair.Value;
                }

                try
                {
                    await ReadDependenciesAsync(connection, database, databaseObjects, dependencies, cancellationToken);
                }
                catch (SqlException exception)
                {
                    logger.LogWarning(exception, "Unable to read dependencies from {Database}", database);
                    warnings.Add($"{database} : les dépendances n’ont pas pu être lues ({exception.Message}).");
                }

                try
                {
                    await ReadForeignKeysAsync(connection, database, databaseObjects, edges, cancellationToken);
                }
                catch (SqlException exception)
                {
                    logger.LogWarning(exception, "Unable to read foreign keys from {Database}", database);
                    warnings.Add($"{database} : les clés étrangères n’ont pas pu être lues ({exception.Message}).");
                }
            }
            catch (SqlException exception)
            {
                logger.LogWarning(exception, "Unable to scan database {Database}", database);
                warnings.Add($"{database} n’a pas pu être analysée : {exception.Message}");
            }
        }

        ResolveDependencies(
            dependencies,
            objectsByKey,
            objectsByQualifiedName,
            nodes,
            edges,
            session.IsServerScope,
            session.Server);

        if (emptyDatabaseCount > 0)
        {
            warnings.Add($"{emptyDatabaseCount} base(s) analysée(s) ne contiennent aucun objet utilisateur visible.");
        }

        var unresolvedCount = nodes.Values.Count(node => !node.IsResolved);
        if (unresolvedCount > 0)
        {
            warnings.Add($"{unresolvedCount} référence(s) externe(s) ou non résolue(s) sont affichées en gris.");
        }

        return new DatabaseMap(
            session.Server,
            session.Database,
            DateTimeOffset.UtcNow,
            nodes.Values
                .OrderBy(node => node.Database, StringComparer.OrdinalIgnoreCase)
                .ThenBy(node => node.Kind)
                .ThenBy(node => node.QualifiedName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            edges
                .GroupBy(edge => new { edge.SourceId, edge.TargetId, edge.Kind })
                .Select(group => group.First())
                .ToList(),
            warnings,
            session.IsServerScope,
            scannedDatabases);
    }

    private static async Task<IReadOnlyList<string>> ReadAccessibleDatabasesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var databases = new List<string>();
        await using var command = CreateCommand(connection, DatabasesQuery);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            databases.Add(reader.GetString(0));
        }

        return databases;
    }

    private static async Task<string?> ReadServerNameAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')); ");
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<Dictionary<int, DatabaseMapNode>> ReadObjectsAsync(
        SqlConnection connection,
        string database,
        bool serverScope,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, DatabaseMapNode>();
        await using var command = CreateCommand(connection, ObjectsQuery);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var objectId = reader.GetInt32(0);
            var schema = reader.GetString(1);
            var name = reader.GetString(2);
            var sqlType = reader.GetString(3).Trim();

            result[objectId] = new DatabaseMapNode(
                ObjectId(database, objectId),
                name,
                serverScope ? $"{database}.{schema}.{name}" : $"{schema}.{name}",
                schema,
                ToKind(sqlType),
                SchemaId(database, schema),
                Database: database,
                SqlObjectId: objectId);
        }

        return result;
    }

    private static async Task ReadDependenciesAsync(
        SqlConnection connection,
        string database,
        IReadOnlyDictionary<int, DatabaseMapNode> objects,
        ICollection<PendingDependency> dependencies,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, DependenciesQuery);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var referencingId = reader.GetInt32(0);
            if (!objects.TryGetValue(referencingId, out var source))
            {
                continue;
            }

            dependencies.Add(new PendingDependency(
                database,
                source,
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                GetNullableString(reader, 2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5)));
        }
    }

    private static void ResolveDependencies(
        IEnumerable<PendingDependency> dependencies,
        IReadOnlyDictionary<string, DatabaseMapNode> objectsByKey,
        IReadOnlyDictionary<string, DatabaseMapNode> objectsByQualifiedName,
        IDictionary<string, DatabaseMapNode> nodes,
        ICollection<DatabaseMapEdge> edges,
        bool serverScope,
        string currentServer)
    {
        var edgeNumber = 0;
        foreach (var dependency in dependencies)
        {
            DatabaseMapNode? target = null;
            var referencesCurrentServer = IsCurrentServer(dependency.ReferencedServer, currentServer);
            if (dependency.ReferencedId is not null && referencesCurrentServer)
            {
                objectsByKey.TryGetValue(ObjectKey(dependency.SourceDatabase, dependency.ReferencedId.Value), out target);
            }

            var targetDatabase = dependency.ReferencedDatabase ?? dependency.SourceDatabase;
            if (target is null &&
                referencesCurrentServer &&
                !string.IsNullOrWhiteSpace(dependency.ReferencedSchema) &&
                !string.IsNullOrWhiteSpace(dependency.ReferencedEntity))
            {
                objectsByQualifiedName.TryGetValue(
                    QualifiedKey(targetDatabase, dependency.ReferencedSchema, dependency.ReferencedEntity),
                    out target);
            }

            if (target is null && !string.IsNullOrWhiteSpace(dependency.ReferencedEntity))
            {
                var schema = dependency.ReferencedSchema ?? "?";
                var parts = new[]
                {
                    dependency.ReferencedServer,
                    serverScope ? targetDatabase : dependency.ReferencedDatabase,
                    schema,
                    dependency.ReferencedEntity
                }.Where(part => !string.IsNullOrWhiteSpace(part));
                var qualifiedName = string.Join('.', parts);
                var externalId = ExternalId(dependency.SourceDatabase, qualifiedName);
                target = new DatabaseMapNode(
                    externalId,
                    dependency.ReferencedEntity,
                    qualifiedName,
                    schema,
                    DatabaseObjectKind.External,
                    IsResolved: false,
                    Database: targetDatabase);
                nodes.TryAdd(externalId, target);
            }

            if (target is null || dependency.Source.Id == target.Id)
            {
                continue;
            }

            edges.Add(new DatabaseMapEdge(
                $"dependency:{++edgeNumber}",
                dependency.Source.Id,
                target.Id,
                DatabaseRelationKind.DependsOn,
                target.Database.Equals(dependency.SourceDatabase, StringComparison.OrdinalIgnoreCase)
                    ? "utilise"
                    : "utilise une autre base"));
        }
    }

    private static async Task ReadForeignKeysAsync(
        SqlConnection connection,
        string database,
        IReadOnlyDictionary<int, DatabaseMapNode> objects,
        ICollection<DatabaseMapEdge> edges,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, ForeignKeysQuery);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var foreignKeyId = reader.GetInt32(0);
            if (!objects.TryGetValue(reader.GetInt32(1), out var source) ||
                !objects.TryGetValue(reader.GetInt32(2), out var target))
            {
                continue;
            }

            edges.Add(new DatabaseMapEdge(
                $"foreign-key:{database}:{foreignKeyId}",
                source.Id,
                target.Id,
                DatabaseRelationKind.ForeignKey,
                reader.GetString(3)));
        }
    }

    private static SqlCommand CreateCommand(SqlConnection connection, string commandText) =>
        new(commandText, connection) { CommandType = CommandType.Text, CommandTimeout = 60 };

    private static string? GetNullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DatabaseObjectKind ToKind(string sqlType) => sqlType switch
    {
        "U" => DatabaseObjectKind.Table,
        "V" => DatabaseObjectKind.View,
        "P" or "PC" => DatabaseObjectKind.Procedure,
        "FN" or "IF" or "TF" => DatabaseObjectKind.Function,
        _ => DatabaseObjectKind.External
    };

    private static string DatabaseId(string database) => $"database:{database}".ToLowerInvariant();
    private static string SchemaId(string database, string schema) => $"schema:{database}:{schema}".ToLowerInvariant();
    private static string ObjectId(string database, int objectId) => $"object:{database}:{objectId}".ToLowerInvariant();
    private static string ObjectKey(string database, int objectId) => $"{database}:{objectId}";
    private static string QualifiedKey(string database, string schema, string entity) => $"{database}:{schema}:{entity}";
    private static string ExternalId(string sourceDatabase, string qualifiedName) =>
        $"external:{sourceDatabase}:{qualifiedName}".ToLowerInvariant();

    private static bool IsCurrentServer(string? referencedServer, string currentServer) =>
        string.IsNullOrWhiteSpace(referencedServer) ||
        referencedServer.Equals(currentServer, StringComparison.OrdinalIgnoreCase);

    private static string ToFriendlyConnectionMessage(SqlException exception) => exception.Number switch
    {
        -2 => "Le délai de connexion est dépassé. Vérifie le serveur, l’instance et le pare-feu.",
        53 or 26 => "Le serveur ou l’instance SQL Server est introuvable.",
        18456 => "L’authentification SQL Server a échoué. Vérifie l’utilisateur et le mot de passe.",
        4060 => "La base demandée est inaccessible ou n’existe pas.",
        229 => "Le compte ne possède pas les permissions nécessaires pour cette opération.",
        _ => $"SQL Server a refusé la connexion : {exception.Message}"
    };

    private sealed record PendingDependency(
        string SourceDatabase,
        DatabaseMapNode Source,
        int? ReferencedId,
        string? ReferencedServer,
        string? ReferencedDatabase,
        string? ReferencedSchema,
        string? ReferencedEntity);
}
