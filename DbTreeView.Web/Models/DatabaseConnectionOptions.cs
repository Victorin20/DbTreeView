using System.ComponentModel.DataAnnotations;
using Microsoft.Data.SqlClient;

namespace DbTreeView.Web.Models;

public sealed class DatabaseConnectionOptions
{
    [Required(ErrorMessage = "Le nom du serveur est obligatoire.")]
    public string Server { get; set; } = @"(localdb)\MSSQLLocalDB";

    [Required(ErrorMessage = "Le nom de la base est obligatoire.")]
    public string Database { get; set; } = string.Empty;

    public bool UseIntegratedSecurity { get; set; } = true;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool Encrypt { get; set; } = true;

    public bool TrustServerCertificate { get; set; } = true;

    public string BuildConnectionString()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server.Trim(),
            InitialCatalog = Database.Trim(),
            IntegratedSecurity = UseIntegratedSecurity,
            Encrypt = Encrypt,
            TrustServerCertificate = TrustServerCertificate,
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "DB Tree View",
            ConnectTimeout = 8,
            CommandTimeout = 60,
            PersistSecurityInfo = false,
            Pooling = true
        };

        if (!UseIntegratedSecurity)
        {
            builder.UserID = Username.Trim();
            builder.Password = Password;
        }

        return builder.ConnectionString;
    }
}
