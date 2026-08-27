using System.Data;
using DbTreeView.Web.Models;
using Microsoft.Data.SqlClient;

namespace DbTreeView.Web.Services;

public sealed class SqlServerConnectedApplicationsService(
    DatabaseSession session,
    ILogger<SqlServerConnectedApplicationsService> logger)
{
    private const string PermissionQuery = """
        SELECT
            CONVERT(int, SERVERPROPERTY('ProductMajorVersion')) AS major_version,
            COALESCE(HAS_PERMS_BY_NAME(NULL, 'SERVER', 'VIEW SERVER STATE'), 0) AS can_view_server_state,
            COALESCE(HAS_PERMS_BY_NAME(NULL, 'SERVER', 'VIEW SERVER PERFORMANCE STATE'), 0) AS can_view_server_performance_state;
        """;

    public async Task<ConnectedApplicationsSnapshot> ReadAsync(
        DatabaseMap map,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsConnected || session.ConnectionString is null)
        {
            throw new InvalidOperationException("Aucune connexion SQL Server active.");
        }

        var scopedDatabases = (map.ScannedDatabases ?? [map.Database])
            .Where(database => !string.IsNullOrWhiteSpace(database))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (scopedDatabases.Length == 0)
        {
            return Empty("Aucune base analysée n’est disponible pour filtrer les connexions.");
        }

        try
        {
            await using var connection = new SqlConnection(session.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            var permission = await ReadPermissionAsync(connection, cancellationToken);
            if (!permission.IsGranted)
            {
                var requiredPermission = permission.MajorVersion >= 16
                    ? "VIEW SERVER PERFORMANCE STATE"
                    : "VIEW SERVER STATE";

                return Empty(
                    $"La liste complète des applications connectées nécessite la permission serveur {requiredPermission}. " +
                    "La cartographie reste disponible sans ce droit.");
            }

            await CreateScopeTableAsync(connection, scopedDatabases, cancellationToken);
            var ownApplicationName = new SqlConnectionStringBuilder(session.ConnectionString).ApplicationName;
            var applications = await ReadApplicationsAsync(connection, ownApplicationName, cancellationToken);
            return new ConnectedApplicationsSnapshot(DateTimeOffset.UtcNow, applications, []);
        }
        catch (SqlException exception)
        {
            logger.LogWarning(exception, "Unable to read connected SQL Server applications");
            return Empty($"Les applications connectées ne peuvent pas être lues : {exception.Message}");
        }
    }

    private static async Task CreateScopeTableAsync(
        SqlConnection connection,
        IReadOnlyList<string> scopedDatabases,
        CancellationToken cancellationToken)
    {
        await using (var createCommand = new SqlCommand(
            "CREATE TABLE #scoped_databases ([name] sysname NOT NULL PRIMARY KEY);",
            connection)
        {
            CommandTimeout = 15
        })
        {
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const int batchSize = 500;
        for (var offset = 0; offset < scopedDatabases.Count; offset += batchSize)
        {
            var batch = scopedDatabases
                .Skip(offset)
                .Take(batchSize)
                .ToArray();
            var parameterNames = batch
                .Select((_, index) => $"@database{index}")
                .ToArray();

            await using var insertCommand = new SqlCommand(
                $"INSERT INTO #scoped_databases ([name]) VALUES {string.Join(", ", parameterNames.Select(name => $"({name})"))};",
                connection)
            {
                CommandTimeout = 15
            };

            for (var index = 0; index < batch.Length; index++)
            {
                insertCommand.Parameters.Add(parameterNames[index], SqlDbType.NVarChar, 128).Value = batch[index];
            }

            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<ConnectedApplication>> ReadApplicationsAsync(
        SqlConnection connection,
        string ownApplicationName,
        CancellationToken cancellationToken)
    {
        const string query = """
            WITH session_snapshot AS
            (
                SELECT
                    d.name AS database_name,
                    COALESCE(NULLIF(LTRIM(RTRIM(s.program_name)), N''), N'Application non déclarée') AS application_name,
                    COALESCE(NULLIF(LTRIM(RTRIM(s.host_name)), N''), N'Machine non déclarée') AS host_name,
                    COALESCE(NULLIF(LTRIM(RTRIM(s.original_login_name)), N''), N'Compte non déclaré') AS login_name,
                    COALESCE(NULLIF(LTRIM(RTRIM(s.client_interface_name)), N''), N'Pilote non déclaré') AS client_interface,
                    LOWER(s.status) AS session_status,
                    CONVERT(bigint, s.open_transaction_count) AS open_transaction_count,
                    s.login_time,
                    CASE
                        WHEN s.last_request_end_time IS NULL
                          OR s.last_request_start_time >= s.last_request_end_time
                            THEN s.last_request_start_time
                        ELSE s.last_request_end_time
                    END AS last_activity_at
                FROM sys.dm_exec_sessions AS s
                INNER JOIN sys.databases AS d ON d.database_id = s.database_id
                INNER JOIN #scoped_databases AS scope ON scope.name = d.name
                WHERE s.is_user_process = 1
                  AND s.session_id <> @@SPID
                  AND (@own_application_name = N'' OR ISNULL(s.program_name, N'') <> @own_application_name)
            )
            SELECT
                database_name,
                application_name,
                host_name,
                login_name,
                client_interface,
                COUNT_BIG(*) AS session_count,
                SUM(CASE WHEN session_status = N'running' THEN CONVERT(bigint, 1) ELSE 0 END) AS active_session_count,
                SUM(CASE WHEN session_status = N'sleeping' THEN CONVERT(bigint, 1) ELSE 0 END) AS sleeping_session_count,
                SUM(open_transaction_count) AS open_transaction_count,
                MIN(login_time) AS first_connected_at,
                MAX(last_activity_at) AS last_activity_at
            FROM session_snapshot
            GROUP BY database_name, application_name, host_name, login_name, client_interface
            ORDER BY session_count DESC, database_name, application_name;
            """;

        await using var command = new SqlCommand(query, connection)
        {
            CommandTimeout = 15
        };
        command.Parameters.Add("@own_application_name", SqlDbType.NVarChar, 128).Value = ownApplicationName;

        var output = new List<ConnectedApplication>();
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ConnectedApplication(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                ReadDateTime(reader, 9),
                ReadDateTime(reader, 10)));
        }

        return output;
    }

    private static async Task<PermissionResult> ReadPermissionAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(PermissionQuery, connection)
        {
            CommandTimeout = 15
        };
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return new PermissionResult(0, false);
        }

        var majorVersion = reader.GetInt32(0);
        var canViewServerState = reader.GetInt32(1) == 1;
        var canViewServerPerformanceState = reader.GetInt32(2) == 1;
        return new PermissionResult(
            majorVersion,
            majorVersion >= 16 ? canViewServerPerformanceState : canViewServerState);
    }

    private static DateTime? ReadDateTime(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    private static ConnectedApplicationsSnapshot Empty(string warning) =>
        new(DateTimeOffset.UtcNow, [], [warning]);

    private sealed record PermissionResult(int MajorVersion, bool IsGranted);
}
