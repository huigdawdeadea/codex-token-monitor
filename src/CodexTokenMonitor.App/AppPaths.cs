namespace CodexTokenMonitor.App;

internal sealed record AppPaths(string CodexHome, string DataHome)
{
    public string DatabasePath => Path.Combine(DataHome, "monitor.db");

    public static AppPaths Resolve()
    {
        var configuredCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = string.IsNullOrWhiteSpace(configuredCodexHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : configuredCodexHome;
        var configuredDataHome = Environment.GetEnvironmentVariable("CODEX_TOKEN_MONITOR_DATA_HOME");
        var dataHome = string.IsNullOrWhiteSpace(configuredDataHome)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexTokenMonitor")
            : configuredDataHome;
        return new AppPaths(Path.GetFullPath(codexHome), Path.GetFullPath(dataHome));
    }
}
