using System.Data;
using DbTreeView.Web.Models;
using Microsoft.Data.SqlClient;

namespace DbTreeView.Web.Services;

public sealed class SqlServerMetadataService(DatabaseSession session, ILogger<SqlServerMetadataService> logger)
{
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

        try
        {
            var connectionString = options.BuildConnectionString();
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DB_NAME();";
            command.CommandTimeout = 10;
            var databaseName = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? options.Database;

            session.Connect(connectionString, options.Server.Trim(), databaseName, connection.ServerVersion);

            return new ConnectionTestResult(
                true,
                $"Connexion établie à {databaseName}.",
                connection.ServerVersion,
                databaseName);
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
        if (!session.IsConnected || session.ConnectionString is null)
        {
            throw new InvalidOperationException("Aucune connexion SQL Server active.");
        }

        await using var connection = new SqlConnection(session.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var nodesByObjectId = await ReadObjectsAsync(connection, cancellationToken);
        var schemaNodes = nodesByObjectId.Values
            .Select(node => node.Schema)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(schema => new DatabaseMapNode(
                SchemaId(schema),
                schema,
                schema,
                schema,
                DatabaseObjectKind.Schema))
            .ToList();

        var nodes = schemaNodes
            .Concat(nodesByObjectId.Values)
            .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);

        var edges = new List<DatabaseMapEdge>();
        var warnings = new List<string>();

        await ReadDependenciesAsync(connection, nodesByObjectId, nodes, edges, cancellationToken);
        await ReadForeignKeysAsync(connection, nodesByObjectId, edges, cancellationToken);

        if (nodesByObjectId.Count == 0)
        {
            warnings.Add("Aucun objet utilisateur n’a été trouvé dans cette base.");
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
            nodes.Values.OrderBy(node => node.Kind).ThenBy(node => node.QualifiedName).ToList(),
            edges.GroupBy(edge => new { edge.SourceId, edge.TargetId, edge.Kind }).Select(group => group.First()).ToList(),
            warnings);
    }

    private static async Task<Dictionary<int, DatabaseMapNode>> ReadObjectsAsync(
        SqlConnection connection,
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
                ObjectId(objectId),
                name,
                $"{schema}.{name}",
                schema,
                ToKind(sqlType),
                SchemaId(schema));
        }

        return result;
    }

    private static async Task ReadDependenciesAsync(
        SqlConnection connection,
        IReadOnlyDictionary<int, DatabaseMapNode> objects,
        IDictionary<string, DatabaseMapNode> nodes,
        ICollection<DatabaseMapEdge> edges,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, DependenciesQuery);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var edgeNumber = 0;

        while (await reader.ReadAsync(cancellationToken))
        {
            var referencingId = reader.GetInt32(0);
            if (!objects.TryGetValue(referencingId, out var source))
            {
                continue;
            }

            DatabaseMapNode? target = null;
            if (!reader.IsDBNull(1) && objects.TryGetValue(reader.GetInt32(1), out var resolvedTarget))
            {
                target = resolvedTarget;
            }
            else
            {
                var serverName = GetNullableString(reader, 2);
                var databaseName = GetNullableString(reader, 3);
                var schemaName = GetNullableString(reader, 4) ?? "?";
                var entityName = GetNullableString(reader, 5);

                if (!string.IsNullOrWhiteSpace(entityName))
                {
                    var parts = new[] { serverName, databaseName, schemaName, entityName }
                        .Where(part => !string.IsNullOrWhiteSpace(part));
                    var qualifiedName = string.Join('.', parts);
                    var externalId = $"external:{qualifiedName}".ToLowerInvariant();
                    target = new DatabaseMapNode(
                        externalId,
                        entityName,
                        qualifiedName,
                        schemaName,
                        DatabaseObjectKind.External,
                        null,
                        false);
                    nodes.TryAdd(externalId, target);
                }
            }

            if (target is null || source.Id == target.Id)
            {
                continue;
            }

            edges.Add(new DatabaseMapEdge(
                $"dependency:{++edgeNumber}",
                source.Id,
                target.Id,
                DatabaseRelationKind.DependsOn,
                "utilise"));
        }
    }

    private static async Task ReadForeignKeysAsync(
        SqlConnection connection,
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
                $"foreign-key:{foreignKeyId}",
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

    private static string ObjectId(int objectId) => $"object:{objectId}";
    private static string SchemaId(string schema) => $"schema:{schema}".ToLowerInvariant();

    private static string ToFriendlyConnectionMessage(SqlException exception) => exception.Number switch
    {
        -2 => "Le délai de connexion est dépassé. Vérifie le serveur, l’instance et le pare-feu.",
        53 or 26 => "Le serveur ou l’instance SQL Server est introuvable.",
        18456 => "L’authentification SQL Server a échoué. Vérifie l’utilisateur et le mot de passe.",
        4060 => "La base demandée est inaccessible ou n’existe pas.",
        229 => "Le compte ne possède pas les permissions nécessaires pour cette opération.",
        _ => $"SQL Server a refusé la connexion : {exception.Message}"
    };
}
