using DbTreeView.Web.Models;

namespace DbTreeView.Web.Services;

public static class DemoDatabaseMapFactory
{
    public static DatabaseMap Create()
    {
        const string schemaId = "schema:dbo";
        var nodes = new List<DatabaseMapNode>
        {
            new(schemaId, "dbo", "dbo", "dbo", DatabaseObjectKind.Schema),
            new("object:1", "usp_GenererFacture", "dbo.usp_GenererFacture", "dbo", DatabaseObjectKind.Procedure, schemaId),
            new("object:2", "usp_ChargerClient", "dbo.usp_ChargerClient", "dbo", DatabaseObjectKind.Procedure, schemaId),
            new("object:3", "vw_CommandesEnCours", "dbo.vw_CommandesEnCours", "dbo", DatabaseObjectKind.View, schemaId),
            new("object:4", "Clients", "dbo.Clients", "dbo", DatabaseObjectKind.Table, schemaId),
            new("object:5", "Commandes", "dbo.Commandes", "dbo", DatabaseObjectKind.Table, schemaId),
            new("object:6", "Factures", "dbo.Factures", "dbo", DatabaseObjectKind.Table, schemaId)
        };

        var edges = new List<DatabaseMapEdge>
        {
            new("dependency:1", "object:1", "object:2", DatabaseRelationKind.DependsOn, "appelle"),
            new("dependency:2", "object:1", "object:3", DatabaseRelationKind.DependsOn, "utilise"),
            new("dependency:3", "object:1", "object:6", DatabaseRelationKind.DependsOn, "écrit"),
            new("dependency:4", "object:2", "object:4", DatabaseRelationKind.DependsOn, "utilise"),
            new("dependency:5", "object:3", "object:5", DatabaseRelationKind.DependsOn, "utilise"),
            new("foreign-key:1", "object:5", "object:4", DatabaseRelationKind.ForeignKey, "FK_Commandes_Clients")
        };

        return new DatabaseMap(
            @"DEMO\SQLSERVER",
            "VentesDemo",
            DateTimeOffset.UtcNow,
            nodes,
            edges,
            ["Mode démonstration : aucune connexion SQL Server n’est utilisée."]);
    }
}
