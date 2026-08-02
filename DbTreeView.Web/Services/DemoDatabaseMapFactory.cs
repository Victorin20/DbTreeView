using DbTreeView.Web.Models;

namespace DbTreeView.Web.Services;

public static class DemoDatabaseMapFactory
{
    public static DatabaseMap Create()
    {
        const string salesDatabaseId = "database:ventesdemo";
        const string referenceDatabaseId = "database:referentieldemo";
        const string salesSchemaId = "schema:ventesdemo:dbo";
        const string referenceSchemaId = "schema:referentieldemo:dbo";

        var nodes = new List<DatabaseMapNode>
        {
            new(salesDatabaseId, "VentesDemo", "VentesDemo", "", DatabaseObjectKind.Database, Database: "VentesDemo"),
            new(referenceDatabaseId, "ReferentielDemo", "ReferentielDemo", "", DatabaseObjectKind.Database, Database: "ReferentielDemo"),
            new(salesSchemaId, "dbo", "VentesDemo.dbo", "dbo", DatabaseObjectKind.Schema, salesDatabaseId, Database: "VentesDemo"),
            new(referenceSchemaId, "dbo", "ReferentielDemo.dbo", "dbo", DatabaseObjectKind.Schema, referenceDatabaseId, Database: "ReferentielDemo"),
            new("object:ventesdemo:1", "usp_GenererFacture", "VentesDemo.dbo.usp_GenererFacture", "dbo", DatabaseObjectKind.Procedure, salesSchemaId, Database: "VentesDemo", SqlObjectId: 1),
            new("object:ventesdemo:2", "usp_ChargerClient", "VentesDemo.dbo.usp_ChargerClient", "dbo", DatabaseObjectKind.Procedure, salesSchemaId, Database: "VentesDemo", SqlObjectId: 2),
            new("object:ventesdemo:3", "vw_CommandesEnCours", "VentesDemo.dbo.vw_CommandesEnCours", "dbo", DatabaseObjectKind.View, salesSchemaId, Database: "VentesDemo", SqlObjectId: 3),
            new("object:referentieldemo:4", "Clients", "ReferentielDemo.dbo.Clients", "dbo", DatabaseObjectKind.Table, referenceSchemaId, Database: "ReferentielDemo", SqlObjectId: 4),
            new("object:ventesdemo:5", "Commandes", "VentesDemo.dbo.Commandes", "dbo", DatabaseObjectKind.Table, salesSchemaId, Database: "VentesDemo", SqlObjectId: 5),
            new("object:ventesdemo:6", "Factures", "VentesDemo.dbo.Factures", "dbo", DatabaseObjectKind.Table, salesSchemaId, Database: "VentesDemo", SqlObjectId: 6)
        };

        var edges = new List<DatabaseMapEdge>
        {
            new("dependency:1", "object:ventesdemo:1", "object:ventesdemo:2", DatabaseRelationKind.DependsOn, "appelle"),
            new("dependency:2", "object:ventesdemo:1", "object:ventesdemo:3", DatabaseRelationKind.DependsOn, "utilise"),
            new("dependency:3", "object:ventesdemo:1", "object:ventesdemo:6", DatabaseRelationKind.DependsOn, "écrit"),
            new("dependency:4", "object:ventesdemo:2", "object:referentieldemo:4", DatabaseRelationKind.DependsOn, "utilise une autre base"),
            new("dependency:5", "object:ventesdemo:3", "object:ventesdemo:5", DatabaseRelationKind.DependsOn, "utilise"),
            new("foreign-key:ventesdemo:1", "object:ventesdemo:6", "object:ventesdemo:5", DatabaseRelationKind.ForeignKey, "FK_Factures_Commandes")
        };

        return new DatabaseMap(
            @"DEMO\SQLSERVER",
            string.Empty,
            DateTimeOffset.UtcNow,
            nodes,
            edges,
            ["Mode démonstration : aucune connexion SQL Server n’est utilisée."],
            true,
            ["ReferentielDemo", "VentesDemo"]);
    }
}
