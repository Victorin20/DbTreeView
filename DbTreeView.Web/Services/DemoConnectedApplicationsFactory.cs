using DbTreeView.Web.Models;

namespace DbTreeView.Web.Services;

public static class DemoConnectedApplicationsFactory
{
    public static ConnectedApplicationsSnapshot Create()
    {
        var now = DateTime.Now;
        return new ConnectedApplicationsSnapshot(
            DateTimeOffset.UtcNow,
            [
                new ConnectedApplication(
                    "VentesDemo", "Portail Ventes", "WEB-PROD-01", "DEMO\\svc_portail",
                    "Microsoft.Data.SqlClient", 18, 3, 15, 0, now.AddHours(-9), now.AddSeconds(-12)),
                new ConnectedApplication(
                    "VentesDemo", "ERP - Facturation nocturne", "BATCH-02", "DEMO\\svc_erp",
                    "Microsoft.Data.SqlClient", 4, 1, 3, 1, now.AddHours(-2), now.AddMinutes(-1)),
                new ConnectedApplication(
                    "ReferentielDemo", "Power BI Desktop", "PC-ANALYSTE-07", "DEMO\\analyste",
                    ".Net SqlClient Data Provider", 2, 1, 1, 0, now.AddMinutes(-48), now.AddSeconds(-35)),
                new ConnectedApplication(
                    "ReferentielDemo", "Microsoft SQL Server Management Studio - Query", "DBA-ADMIN-01", "DEMO\\dba",
                    ".Net SqlClient Data Provider", 1, 0, 1, 0, now.AddMinutes(-15), now.AddMinutes(-4))
            ],
            []);
    }
}
