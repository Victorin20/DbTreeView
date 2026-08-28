using System.Text.Json.Serialization;

namespace DbTreeView.Web.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DatabaseObjectKind
{
    Database,
    Schema,
    Table,
    View,
    Procedure,
    Function,
    External
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DatabaseRelationKind
{
    DependsOn,
    Calls,
    ForeignKey
}

public sealed record DatabaseMapNode(
    string Id,
    string Name,
    string QualifiedName,
    string Schema,
    DatabaseObjectKind Kind,
    string? ParentId = null,
    bool IsResolved = true,
    string Database = "",
    int? SqlObjectId = null);

public sealed record DatabaseMapEdge(
    string Id,
    string SourceId,
    string TargetId,
    DatabaseRelationKind Kind,
    string Label,
    bool IsInferred = false);

public sealed record DatabaseMap(
    string Server,
    string Database,
    DateTimeOffset ScannedAt,
    IReadOnlyList<DatabaseMapNode> Nodes,
    IReadOnlyList<DatabaseMapEdge> Edges,
    IReadOnlyList<string> Warnings,
    bool IsServerScope = false,
    IReadOnlyList<string>? ScannedDatabases = null)
{
    public int ObjectCount => Nodes.Count(node => node.Kind is not (DatabaseObjectKind.Database or DatabaseObjectKind.Schema));
    public int ProcedureCount => Nodes.Count(node => node.Kind is DatabaseObjectKind.Procedure);
    public int ViewCount => Nodes.Count(node => node.Kind is DatabaseObjectKind.View);
    public int TableCount => Nodes.Count(node => node.Kind is DatabaseObjectKind.Table);
    public int DatabaseCount => ScannedDatabases?.Count ?? Nodes.Count(node => node.Kind is DatabaseObjectKind.Database);
    public string ScopeName => IsServerScope ? Server : Database;
}

public sealed record ConnectionTestResult(
    bool IsSuccess,
    string Message,
    string? ServerVersion = null,
    string? DatabaseName = null,
    int DatabaseCount = 0);
