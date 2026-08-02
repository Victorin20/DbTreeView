using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using DbTreeView.Web.Models;
using Microsoft.Data.SqlClient;

namespace DbTreeView.Web.Services;

public sealed partial class SqlServerPerformanceService(
    DatabaseSession session,
    ILogger<SqlServerPerformanceService> logger)
{
    private const int QueryStoreWindowDays = 7;

    private const string ProcedureStatsQuery = """
        SELECT
            ps.object_id,
            s.name AS schema_name,
            p.name AS object_name,
            MIN(ps.cached_time) AS cached_time,
            MAX(ps.last_execution_time) AS last_execution_time,
            SUM(ps.execution_count) AS execution_count,
            SUM(ps.total_elapsed_time) AS total_elapsed_microseconds,
            SUM(ps.total_worker_time) AS total_cpu_microseconds,
            SUM(ps.total_logical_reads) AS total_logical_reads,
            SUM(ps.total_logical_writes) AS total_logical_writes
        FROM sys.dm_exec_procedure_stats AS ps
        INNER JOIN sys.procedures AS p ON p.object_id = ps.object_id
        INNER JOIN sys.schemas AS s ON s.schema_id = p.schema_id
        WHERE ps.database_id = DB_ID()
          AND p.is_ms_shipped = 0
        GROUP BY ps.object_id, s.name, p.name
        ORDER BY total_cpu_microseconds DESC;
        """;

    private const string TableStatsQuery = """
        SELECT
            t.object_id,
            s.name AS schema_name,
            t.name AS object_name,
            COALESCE(p.row_count, 0) AS row_count,
            COALESCE(p.reserved_page_count, 0) * 8.0 / 1024.0 AS reserved_size_mb,
            COALESCE(u.user_seeks, 0) AS user_seeks,
            COALESCE(u.user_scans, 0) AS user_scans,
            COALESCE(u.user_lookups, 0) AS user_lookups,
            COALESCE(u.user_updates, 0) AS user_updates,
            u.last_read,
            u.last_write
        FROM sys.tables AS t
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        LEFT JOIN
        (
            SELECT
                object_id,
                SUM(CASE WHEN index_id IN (0, 1) THEN row_count ELSE 0 END) AS row_count,
                SUM(reserved_page_count) AS reserved_page_count
            FROM sys.dm_db_partition_stats
            GROUP BY object_id
        ) AS p ON p.object_id = t.object_id
        LEFT JOIN
        (
            SELECT
                object_id,
                SUM(user_seeks) AS user_seeks,
                SUM(user_scans) AS user_scans,
                SUM(user_lookups) AS user_lookups,
                SUM(user_updates) AS user_updates,
                MAX(last_read_values.last_read) AS last_read,
                MAX(last_user_update) AS last_write
            FROM sys.dm_db_index_usage_stats
            OUTER APPLY
            (
                SELECT MAX(read_time) AS last_read
                FROM (VALUES (last_user_seek), (last_user_scan), (last_user_lookup)) AS read_times(read_time)
            ) AS last_read_values
            WHERE database_id = DB_ID()
            GROUP BY object_id
        ) AS u ON u.object_id = t.object_id
        WHERE t.is_ms_shipped = 0
        ORDER BY COALESCE(u.user_scans, 0) DESC;
        """;

    private const string QueryStoreStateQuery = """
        SELECT actual_state_desc
        FROM sys.database_query_store_options;
        """;

    private const string QueryStoreStatsQuery = """
        SELECT TOP (25)
            q.query_id,
            q.object_id,
            CASE
                WHEN q.object_id > 0 THEN
                    COALESCE(OBJECT_SCHEMA_NAME(q.object_id), '?') + '.' + COALESCE(OBJECT_NAME(q.object_id), '?')
                ELSE 'Requête ad hoc'
            END AS object_name,
            qt.query_sql_text,
            SUM(rs.count_executions) AS execution_count,
            SUM(CONVERT(float, rs.avg_duration) * rs.count_executions) / 1000.0 AS total_duration_ms,
            SUM(CONVERT(float, rs.avg_duration) * rs.count_executions)
                / NULLIF(SUM(rs.count_executions), 0) / 1000.0 AS average_duration_ms,
            SUM(CONVERT(float, rs.avg_cpu_time) * rs.count_executions)
                / NULLIF(SUM(rs.count_executions), 0) / 1000.0 AS average_cpu_ms,
            SUM(CONVERT(float, rs.avg_logical_io_reads) * rs.count_executions)
                / NULLIF(SUM(rs.count_executions), 0) AS average_logical_reads,
            MAX(rs.last_execution_time) AS last_execution_time
        FROM sys.query_store_query AS q
        INNER JOIN sys.query_store_query_text AS qt ON qt.query_text_id = q.query_text_id
        INNER JOIN sys.query_store_plan AS p ON p.query_id = q.query_id
        INNER JOIN sys.query_store_runtime_stats AS rs ON rs.plan_id = p.plan_id
        INNER JOIN sys.query_store_runtime_stats_interval AS rsi
            ON rsi.runtime_stats_interval_id = rs.runtime_stats_interval_id
        WHERE rsi.end_time >= DATEADD(day, -7, SYSUTCDATETIME())
        GROUP BY q.query_id, q.object_id, qt.query_sql_text
        ORDER BY total_duration_ms DESC;
        """;

    public async Task<PerformanceSnapshot> ReadAsync(
        DatabaseMap map,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsConnected || session.ConnectionString is null)
        {
            throw new InvalidOperationException("Aucune connexion SQL Server active.");
        }

        var warnings = new List<string>();
        var procedures = new List<ProcedurePerformance>();
        var tables = new List<TablePerformance>();
        var queries = new List<QueryPerformance>();
        var queryStoreStates = new List<string>();
        var nodeIds = map.Nodes
            .Where(node => node.SqlObjectId is not null && !string.IsNullOrWhiteSpace(node.Database))
            .ToDictionary(
                node => NodeKey(node.Database, node.SqlObjectId!.Value),
                node => node.Id,
                StringComparer.OrdinalIgnoreCase);

        foreach (var database in session.Databases)
        {
            try
            {
                await using var connection = new SqlConnection(session.GetConnectionString(database));
                await connection.OpenAsync(cancellationToken);

                await TryCollectAsync(
                    $"statistiques des procédures de {database}",
                    warnings,
                    () => ReadProceduresAsync(connection, database, nodeIds, procedures, cancellationToken));

                await TryCollectAsync(
                    $"activité des tables de {database}",
                    warnings,
                    () => ReadTablesAsync(connection, database, nodeIds, tables, cancellationToken));

                var databaseQueryStoreState = "INCONNU";
                await TryCollectAsync(
                    $"état de Query Store de {database}",
                    warnings,
                    async () => databaseQueryStoreState = await ReadQueryStoreStateAsync(connection, cancellationToken));
                queryStoreStates.Add(databaseQueryStoreState);

                if (databaseQueryStoreState is "READ_WRITE" or "READ_ONLY")
                {
                    await TryCollectAsync(
                        $"historique Query Store de {database}",
                        warnings,
                        () => ReadQueryStoreAsync(connection, database, queries, cancellationToken));
                }
                else if (databaseQueryStoreState == "OFF")
                {
                    warnings.Add($"{database} : Query Store est désactivé.");
                }
                else
                {
                    warnings.Add($"{database} : l’état de Query Store n’a pas pu être déterminé.");
                }
            }
            catch (SqlException exception)
            {
                logger.LogWarning(exception, "Unable to collect performance data from {Database}", database);
                warnings.Add($"{database} : la collecte des performances a échoué ({exception.Message}).");
            }
        }

        var queryStoreState = SummarizeQueryStoreStates(queryStoreStates);
        var topQueries = queries
            .OrderByDescending(query => query.TotalDurationMs)
            .Take(25)
            .ToArray();

        var impacts = BuildNodeImpacts(map, procedures, tables);

        return new PerformanceSnapshot(
            DateTimeOffset.UtcNow,
            queryStoreState,
            QueryStoreWindowDays,
            procedures,
            tables,
            topQueries,
            impacts,
            warnings);
    }

    private async Task TryCollectAsync(string source, List<string> warnings, Func<Task> collector)
    {
        try
        {
            await collector();
        }
        catch (SqlException exception)
        {
            logger.LogWarning(exception, "Unable to read SQL Server {PerformanceSource}", source);
            warnings.Add(exception.Number == 229
                ? $"Accès refusé aux {source}. Le compte SQL doit disposer des droits de consultation des performances."
                : $"Les {source} ne sont pas disponibles : {exception.Message}");
        }
    }

    private static async Task ReadProceduresAsync(
        SqlConnection connection,
        string database,
        IReadOnlyDictionary<string, string> nodeIds,
        List<ProcedurePerformance> output,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, ProcedureStatsQuery);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var executions = reader.GetInt64(5);
            var elapsedMs = reader.GetInt64(6) / 1_000d;
            var cpuMs = reader.GetInt64(7) / 1_000d;
            var reads = reader.GetInt64(8);
            var averageElapsed = executions == 0 ? 0 : elapsedMs / executions;
            var averageCpu = executions == 0 ? 0 : cpuMs / executions;
            var averageReads = executions == 0 ? 0 : (double)reads / executions;
            var heat = ClassifyProcedure(averageElapsed, averageCpu, averageReads, cpuMs, executions);

            output.Add(new ProcedurePerformance(
                reader.GetInt32(0),
                ResolveNodeId(database, reader.GetInt32(0), nodeIds),
                reader.GetString(1),
                reader.GetString(2),
                executions,
                elapsedMs,
                cpuMs,
                reads,
                reader.GetInt64(9),
                ReadDateTimeOffset(reader, 3),
                ReadDateTimeOffset(reader, 4),
                heat,
                RecommendProcedure(averageElapsed, averageCpu, averageReads, executions),
                database));
        }
    }

    private static async Task ReadTablesAsync(
        SqlConnection connection,
        string database,
        IReadOnlyDictionary<string, string> nodeIds,
        List<TablePerformance> output,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, TableStatsQuery);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var seeks = reader.GetInt64(5);
            var scans = reader.GetInt64(6);
            var lookups = reader.GetInt64(7);
            var updates = reader.GetInt64(8);
            var reads = seeks + scans + lookups;
            var scanRatio = reads == 0 ? 0 : (double)scans / reads;
            var heat = ClassifyTable(reads, scans, updates, scanRatio);

            output.Add(new TablePerformance(
                reader.GetInt32(0),
                ResolveNodeId(database, reader.GetInt32(0), nodeIds),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture),
                seeks,
                scans,
                lookups,
                updates,
                ReadDateTimeOffset(reader, 9),
                ReadDateTimeOffset(reader, 10),
                heat,
                RecommendTable(reads, scans, updates, scanRatio),
                database));
        }
    }

    private static async Task<string> ReadQueryStoreStateAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, QueryStoreStateQuery);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        var state = Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(state) ? "INCONNU" : state;
    }

    private static async Task ReadQueryStoreAsync(
        SqlConnection connection,
        string database,
        List<QueryPerformance> output,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, QueryStoreStatsQuery);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new QueryPerformance(
                reader.GetInt64(0),
                reader.GetInt32(1),
                $"{database}.{reader.GetString(2)}",
                NormalizeSql(reader.GetString(3)),
                reader.GetInt64(4),
                Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture),
                Convert.ToDouble(reader.GetValue(6), CultureInfo.InvariantCulture),
                Convert.ToDouble(reader.GetValue(7), CultureInfo.InvariantCulture),
                Convert.ToDouble(reader.GetValue(8), CultureInfo.InvariantCulture),
                ReadDateTimeOffset(reader, 9),
                database));
        }
    }

    private static IReadOnlyList<NodePerformanceImpact> BuildNodeImpacts(
        DatabaseMap map,
        IReadOnlyList<ProcedurePerformance> procedures,
        IReadOnlyList<TablePerformance> tables)
    {
        var aggregates = new Dictionary<string, ImpactAggregate>(StringComparer.Ordinal);
        var outgoing = map.Edges
            .Where(edge => edge.Kind == DatabaseRelationKind.DependsOn)
            .ToLookup(edge => edge.SourceId, edge => edge.TargetId, StringComparer.Ordinal);

        foreach (var procedure in procedures)
        {
            AddImpact(aggregates, procedure.NodeId, procedure, false);

            var visited = new HashSet<string>(StringComparer.Ordinal) { procedure.NodeId };
            var queue = new Queue<string>();
            queue.Enqueue(procedure.NodeId);

            while (queue.TryDequeue(out var source))
            {
                foreach (var target in outgoing[source])
                {
                    if (!visited.Add(target))
                    {
                        continue;
                    }

                    AddImpact(aggregates, target, procedure, true);
                    queue.Enqueue(target);
                }
            }
        }

        foreach (var table in tables)
        {
            if (!aggregates.TryGetValue(table.NodeId, out var aggregate))
            {
                aggregate = new ImpactAggregate();
                aggregates[table.NodeId] = aggregate;
            }

            aggregate.Heat = Max(aggregate.Heat, table.Heat);
            aggregate.TableRecommendation = table.Recommendation;
        }

        return aggregates
            .Select(pair => pair.Value.ToImpact(pair.Key))
            .OrderByDescending(impact => impact.Heat)
            .ThenByDescending(impact => impact.TotalCpuMs)
            .ToArray();
    }

    private static void AddImpact(
        IDictionary<string, ImpactAggregate> aggregates,
        string nodeId,
        ProcedurePerformance procedure,
        bool isDerived)
    {
        if (!aggregates.TryGetValue(nodeId, out var aggregate))
        {
            aggregate = new ImpactAggregate { IsDerived = isDerived };
            aggregates[nodeId] = aggregate;
        }

        if (!aggregate.ContributorIds.Add(procedure.NodeId))
        {
            return;
        }

        aggregate.IsDerived |= isDerived;
        aggregate.Heat = Max(aggregate.Heat, procedure.Heat);
        aggregate.ExecutionCount += procedure.ExecutionCount;
        aggregate.TotalElapsedMs += procedure.TotalElapsedMs;
        aggregate.TotalCpuMs += procedure.TotalCpuMs;
        aggregate.TotalLogicalReads += procedure.TotalLogicalReads;
    }

    private static PerformanceHeat ClassifyProcedure(
        double averageElapsedMs,
        double averageCpuMs,
        double averageLogicalReads,
        double totalCpuMs,
        long executions)
    {
        if (executions == 0)
        {
            return PerformanceHeat.None;
        }

        if (averageElapsedMs >= 2_000 || averageCpuMs >= 1_000 || averageLogicalReads >= 100_000 || totalCpuMs >= 60_000)
        {
            return PerformanceHeat.Critical;
        }

        if (averageElapsedMs >= 500 || averageCpuMs >= 250 || averageLogicalReads >= 20_000 || totalCpuMs >= 10_000)
        {
            return PerformanceHeat.High;
        }

        if (averageElapsedMs >= 100 || averageCpuMs >= 50 || averageLogicalReads >= 5_000 || totalCpuMs >= 1_000)
        {
            return PerformanceHeat.Medium;
        }

        return PerformanceHeat.Low;
    }

    private static PerformanceHeat ClassifyTable(long reads, long scans, long updates, double scanRatio)
    {
        if (reads == 0 && updates == 0)
        {
            return PerformanceHeat.None;
        }

        if (scans >= 10_000 && scanRatio >= 0.75)
        {
            return PerformanceHeat.Critical;
        }

        if (scans >= 1_000 && scanRatio >= 0.65)
        {
            return PerformanceHeat.High;
        }

        if ((scans >= 100 && scanRatio >= 0.5) || updates > Math.Max(1_000, reads * 2))
        {
            return PerformanceHeat.Medium;
        }

        return PerformanceHeat.Low;
    }

    private static string RecommendProcedure(
        double averageElapsedMs,
        double averageCpuMs,
        double averageLogicalReads,
        long executions)
    {
        if (averageLogicalReads >= 20_000)
        {
            return "Lectures logiques élevées : vérifier les prédicats, les scans et les index utiles dans le plan d’exécution.";
        }

        if (averageElapsedMs >= 500 && averageCpuMs < averageElapsedMs * 0.4)
        {
            return "Durée élevée mais CPU modéré : rechercher des attentes, blocages ou accès disque lents.";
        }

        if (averageCpuMs >= 250)
        {
            return "CPU moyen élevé : examiner le plan, les tris, agrégations et conversions implicites.";
        }

        if (executions >= 10_000 && averageElapsedMs >= 20)
        {
            return "Procédure très fréquente : une petite amélioration par appel peut produire un gain important.";
        }

        return "Aucun signal fort selon les seuils actuels ; surveiller l’évolution après les changements.";
    }

    private static string RecommendTable(long reads, long scans, long updates, double scanRatio)
    {
        if (reads == 0 && updates == 0)
        {
            return "Aucune activité observée dans les compteurs actuellement disponibles.";
        }

        if (scans >= 100 && scanRatio >= 0.5)
        {
            return "Part de scans élevée : examiner les plans avant d’ajouter ou modifier un index.";
        }

        if (updates > Math.Max(1_000, reads * 2))
        {
            return "Écritures dominantes : contrôler le coût de maintenance et les index peu utilisés.";
        }

        return "Activité de lecture principalement ciblée ; aucune alerte automatique forte.";
    }

    private static SqlCommand CreateCommand(SqlConnection connection, string query) => new(query, connection)
    {
        CommandTimeout = 30
    };

    private static DateTimeOffset? ReadDateTimeOffset(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetValue(ordinal) switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(value),
            _ => null
        };
    }

    private static string NormalizeSql(string sql)
    {
        var normalized = WhitespaceRegex().Replace(sql, " ").Trim();
        return normalized.Length <= 320 ? normalized : $"{normalized[..317]}…";
    }

    private static PerformanceHeat Max(PerformanceHeat left, PerformanceHeat right) => left >= right ? left : right;

    private static string NodeKey(string database, int objectId) => $"{database}:{objectId}";

    private static string ResolveNodeId(
        string database,
        int objectId,
        IReadOnlyDictionary<string, string> nodeIds) =>
        nodeIds.GetValueOrDefault(NodeKey(database, objectId), $"object:{database}:{objectId}".ToLowerInvariant());

    private static string SummarizeQueryStoreStates(IReadOnlyCollection<string> states)
    {
        if (states.Count == 0)
        {
            return "INCONNU";
        }

        var distinct = states.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return distinct.Length == 1 ? distinct[0] : "MIXTE";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed class ImpactAggregate
    {
        public HashSet<string> ContributorIds { get; } = [];
        public PerformanceHeat Heat { get; set; }
        public bool IsDerived { get; set; }
        public long ExecutionCount { get; set; }
        public double TotalElapsedMs { get; set; }
        public double TotalCpuMs { get; set; }
        public long TotalLogicalReads { get; set; }
        public string? TableRecommendation { get; set; }

        public NodePerformanceImpact ToImpact(string nodeId)
        {
            var averageElapsed = ExecutionCount == 0 ? 0 : TotalElapsedMs / ExecutionCount;
            var averageCpu = ExecutionCount == 0 ? 0 : TotalCpuMs / ExecutionCount;
            var averageReads = ExecutionCount == 0 ? 0 : (double)TotalLogicalReads / ExecutionCount;
            var summary = ContributorIds.Count switch
            {
                > 0 when IsDerived => $"Impact estimé à partir de {ContributorIds.Count} procédure(s) dépendante(s).",
                > 0 => "Mesures directes du cache des procédures.",
                _ => TableRecommendation ?? "Aucune activité observée."
            };

            if (IsDerived && !string.IsNullOrWhiteSpace(TableRecommendation))
            {
                summary = $"{summary} {TableRecommendation}";
            }

            return new NodePerformanceImpact(
                nodeId,
                Heat,
                IsDerived,
                ContributorIds.Count,
                ExecutionCount,
                TotalElapsedMs,
                TotalCpuMs,
                averageElapsed,
                averageCpu,
                averageReads,
                summary);
        }
    }
}
