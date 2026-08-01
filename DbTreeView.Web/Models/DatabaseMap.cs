using System.Text.Json.Serialization;

namespace DbTreeView.Web.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DatabaseObjectKind
{
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
    ForeignKey
}

public sealed record DatabaseMapNode(
    string Id,
    string Name,
    string QualifiedName,
    string Schema,
    DatabaseObjectKind Kind,
    string? ParentId = null,
    bool IsResolved = true);

public sealed record DatabaseMapEdge(
    string Id,
    string SourceId,
    string TargetId,
    DatabaseRelationKind Kind,
    string Label);

public sealed record DatabaseMap(
    string Server,
    string Database,
    DateTimeOffset ScannedAt,
    IReadOnlyList<DatabaseMapNode> Nodes,
    IReadOnlyList<DatabaseMapEdge> Edges,
    IReadOnlyList<string> Warnings)
{
    public int ObjectCount => Nodes.Count(node => node.Kind is not DatabaseObjectKind.Schema);
    public int ProcedureCount => Nodes.Count(node => node.Kind is DatabaseObjectKind.Procedure);
    public int ViewCount => Nodes.Count(node => node.Kind is DatabaseObjectKind.View);
    public int TableCount => Nodes.Count(node => node.Kind is DatabaseObjectKind.Table);
}

public sealed record ConnectionTestResult(
    bool IsSuccess,
    string Message,
    string? ServerVersion = null,
    string? DatabaseName = null);
