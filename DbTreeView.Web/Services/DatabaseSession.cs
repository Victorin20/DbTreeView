namespace DbTreeView.Web.Services;

public sealed class DatabaseSession
{
    public bool IsConnected => !string.IsNullOrWhiteSpace(ConnectionString);
    public string? ConnectionString { get; private set; }
    public string Server { get; private set; } = string.Empty;
    public string Database { get; private set; } = string.Empty;
    public string ServerVersion { get; private set; } = string.Empty;

    public void Connect(string connectionString, string server, string database, string serverVersion)
    {
        ConnectionString = connectionString;
        Server = server;
        Database = database;
        ServerVersion = serverVersion;
    }

    public void Clear()
    {
        ConnectionString = null;
        Server = string.Empty;
        Database = string.Empty;
        ServerVersion = string.Empty;
    }
}
