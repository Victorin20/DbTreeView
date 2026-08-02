using Microsoft.Data.SqlClient;

namespace DbTreeView.Web.Services;

public sealed class DatabaseSession
{
    public bool IsConnected => !string.IsNullOrWhiteSpace(ConnectionString);
    public string? ConnectionString { get; private set; }
    public string Server { get; private set; } = string.Empty;
    public string Database { get; private set; } = string.Empty;
    public string ServerVersion { get; private set; } = string.Empty;
    public bool IsServerScope { get; private set; }
    public IReadOnlyList<string> Databases { get; private set; } = [];

    public void Connect(
        string connectionString,
        string server,
        string database,
        string serverVersion,
        bool isServerScope,
        IReadOnlyList<string> databases)
    {
        ConnectionString = connectionString;
        Server = server;
        Database = database;
        ServerVersion = serverVersion;
        IsServerScope = isServerScope;
        Databases = databases;
    }

    public string GetConnectionString(string database)
    {
        if (ConnectionString is null)
        {
            throw new InvalidOperationException("Aucune connexion SQL Server active.");
        }

        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = database
        };
        return builder.ConnectionString;
    }

    public void Clear()
    {
        ConnectionString = null;
        Server = string.Empty;
        Database = string.Empty;
        ServerVersion = string.Empty;
        IsServerScope = false;
        Databases = [];
    }
}
