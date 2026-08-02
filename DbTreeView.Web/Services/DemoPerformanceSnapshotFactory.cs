using DbTreeView.Web.Models;

namespace DbTreeView.Web.Services;

public static class DemoPerformanceSnapshotFactory
{
    public static PerformanceSnapshot Create()
    {
        var now = DateTimeOffset.UtcNow;
        var procedures = new List<ProcedurePerformance>
        {
            new(
                1,
                "object:ventesdemo:1",
                "dbo",
                "usp_GenererFacture",
                1_240,
                1_899_680,
                115_320,
                38_440_000,
                84_700,
                now.AddDays(-2),
                now.AddMinutes(-3),
                PerformanceHeat.Critical,
                "Lectures logiques élevées : vérifier les prédicats, les scans et les index utiles dans le plan d’exécution.",
                "VentesDemo"),
            new(
                2,
                "object:ventesdemo:2",
                "dbo",
                "usp_ChargerClient",
                6_820,
                688_820,
                79_500,
                44_330_000,
                0,
                now.AddHours(-18),
                now.AddMinutes(-1),
                PerformanceHeat.High,
                "Procédure très fréquente : une petite amélioration par appel peut produire un gain important.",
                "VentesDemo")
        };

        var tables = new List<TablePerformance>
        {
            new(4, "object:referentieldemo:4", "dbo", "Clients", 185_420, 92.4, 451_200, 5_830, 12_400, 8_320,
                now.AddMinutes(-1), now.AddMinutes(-8), PerformanceHeat.Low,
                "Activité de lecture principalement ciblée ; aucune alerte automatique forte.", "ReferentielDemo"),
            new(5, "object:ventesdemo:5", "dbo", "Commandes", 2_430_500, 1_284.7, 84_100, 42_600, 3_450, 12_840,
                now.AddSeconds(-42), now.AddMinutes(-2), PerformanceHeat.Critical,
                "Part de scans élevée : examiner les plans avant d’ajouter ou modifier un index.", "VentesDemo"),
            new(6, "object:ventesdemo:6", "dbo", "Factures", 934_250, 648.2, 195_400, 9_820, 1_240, 28_900,
                now.AddMinutes(-3), now.AddSeconds(-36), PerformanceHeat.Medium,
                "Activité de lecture principalement ciblée ; aucune alerte automatique forte.", "VentesDemo")
        };

        var queries = new List<QueryPerformance>
        {
            new(103, 1, "VentesDemo.dbo.usp_GenererFacture",
                "SELECT c.Id, c.Total, cl.Nom FROM dbo.Commandes AS c INNER JOIN dbo.Clients AS cl ON cl.Id = c.ClientId WHERE c.Statut = @statut",
                1_240, 1_564_300, 1_261.5, 88.4, 27_850, now.AddMinutes(-3), "VentesDemo"),
            new(118, 2, "VentesDemo.dbo.usp_ChargerClient",
                "SELECT Id, Nom, Email FROM dbo.Clients WHERE Email = @email",
                6_820, 524_600, 76.9, 10.2, 6_430, now.AddMinutes(-1), "VentesDemo")
        };

        var impacts = new List<NodePerformanceImpact>
        {
            new("object:ventesdemo:1", PerformanceHeat.Critical, false, 1, 1_240, 1_899_680, 115_320, 1_532, 93, 31_000, "Mesures directes du cache des procédures."),
            new("object:ventesdemo:2", PerformanceHeat.High, false, 1, 6_820, 688_820, 79_500, 101, 12, 6_500, "Mesures directes du cache des procédures."),
            new("object:ventesdemo:3", PerformanceHeat.Critical, true, 1, 1_240, 1_899_680, 115_320, 1_532, 93, 31_000, "Impact estimé à partir de 1 procédure dépendante."),
            new("object:referentieldemo:4", PerformanceHeat.High, true, 2, 8_060, 2_588_500, 194_820, 321, 24, 10_270, "Impact estimé à partir de 2 procédures dépendantes."),
            new("object:ventesdemo:5", PerformanceHeat.Critical, true, 1, 1_240, 1_899_680, 115_320, 1_532, 93, 31_000, "Impact estimé à partir de 1 procédure dépendante. Part de scans élevée."),
            new("object:ventesdemo:6", PerformanceHeat.Critical, true, 1, 1_240, 1_899_680, 115_320, 1_532, 93, 31_000, "Impact estimé à partir de 1 procédure dépendante.")
        };

        return new PerformanceSnapshot(
            now,
            "READ_WRITE",
            7,
            procedures,
            tables,
            queries,
            impacts,
            []);
    }
}
