namespace DbTreeView.Web.Models;

public sealed record ConnectedApplication(
    string Database,
    string ApplicationName,
    string HostName,
    string LoginName,
    string ClientInterface,
    long SessionCount,
    long ActiveSessionCount,
    long SleepingSessionCount,
    long OpenTransactionCount,
    DateTime? FirstConnectedAt,
    DateTime? LastActivityAt);

public sealed record ConnectedApplicationsSnapshot(
    DateTimeOffset CollectedAt,
    IReadOnlyList<ConnectedApplication> Applications,
    IReadOnlyList<string> Warnings)
{
    public int ApplicationCount => Applications
        .Select(item => item.ApplicationName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public int DatabaseCount => Applications
        .Select(item => item.Database)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public long TotalSessionCount => Applications.Sum(item => item.SessionCount);
    public long ActiveSessionCount => Applications.Sum(item => item.ActiveSessionCount);
}
