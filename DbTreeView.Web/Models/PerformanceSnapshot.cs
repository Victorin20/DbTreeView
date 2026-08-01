using System.Text.Json.Serialization;

namespace DbTreeView.Web.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceHeat
{
    None,
    Low,
    Medium,
    High,
    Critical
}

public sealed record ProcedurePerformance(
    int ObjectId,
    string NodeId,
    string Schema,
    string Name,
    long ExecutionCount,
    double TotalElapsedMs,
    double TotalCpuMs,
    long TotalLogicalReads,
    long TotalLogicalWrites,
    DateTimeOffset? CachedAt,
    DateTimeOffset? LastExecutionAt,
    PerformanceHeat Heat,
    string Recommendation)
{
    public string QualifiedName => $"{Schema}.{Name}";
    public double AverageElapsedMs => ExecutionCount == 0 ? 0 : TotalElapsedMs / ExecutionCount;
    public double AverageCpuMs => ExecutionCount == 0 ? 0 : TotalCpuMs / ExecutionCount;
    public double AverageLogicalReads => ExecutionCount == 0 ? 0 : (double)TotalLogicalReads / ExecutionCount;
}

public sealed record TablePerformance(
    int ObjectId,
    string NodeId,
    string Schema,
    string Name,
    long RowCount,
    double ReservedSizeMb,
    long Seeks,
    long Scans,
    long Lookups,
    long Updates,
    DateTimeOffset? LastReadAt,
    DateTimeOffset? LastWriteAt,
    PerformanceHeat Heat,
    string Recommendation)
{
    public string QualifiedName => $"{Schema}.{Name}";
    public long Reads => Seeks + Scans + Lookups;
    public double ScanRatio => Reads == 0 ? 0 : (double)Scans / Reads;
}

public sealed record QueryPerformance(
    long QueryId,
    int ObjectId,
    string ObjectName,
    string SqlText,
    long ExecutionCount,
    double TotalDurationMs,
    double AverageDurationMs,
    double AverageCpuMs,
    double AverageLogicalReads,
    DateTimeOffset? LastExecutionAt);

public sealed record NodePerformanceImpact(
    string NodeId,
    PerformanceHeat Heat,
    bool IsDerived,
    int ContributorCount,
    long ExecutionCount,
    double TotalElapsedMs,
    double TotalCpuMs,
    double AverageElapsedMs,
    double AverageCpuMs,
    double AverageLogicalReads,
    string Summary);

public sealed record PerformanceSnapshot(
    DateTimeOffset CollectedAt,
    string QueryStoreState,
    int QueryStoreWindowDays,
    IReadOnlyList<ProcedurePerformance> Procedures,
    IReadOnlyList<TablePerformance> Tables,
    IReadOnlyList<QueryPerformance> Queries,
    IReadOnlyList<NodePerformanceImpact> NodeImpacts,
    IReadOnlyList<string> Warnings)
{
    public long TotalProcedureExecutions => Procedures.Sum(item => item.ExecutionCount);
    public int ActiveProcedureCount => Procedures.Count(item => item.ExecutionCount > 0);
    public int HotObjectCount => NodeImpacts.Count(item => item.Heat >= PerformanceHeat.High);
    public double TotalCpuSeconds => Procedures.Sum(item => item.TotalCpuMs) / 1_000d;
}
