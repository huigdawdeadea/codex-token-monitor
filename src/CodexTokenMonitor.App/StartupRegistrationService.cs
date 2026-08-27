using Microsoft.Win32;

namespace CodexTokenMonitor.App;

internal sealed record StartupRegistrationStatus(bool IsEnabled, bool CanEnable, string? Reason);

internal sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexTokenMonitor";
    private readonly string _executablePath = Environment.ProcessPath
        ?? throw new InvalidOperationException("The application executable path is unavailable.");

    public StartupRegistrationStatus GetStatus()
    {
        var canEnable = IsPublishedLocation(_executablePath);
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var current = key?.GetValue(ValueName) as string;
        var enabled = string.Equals(current, BuildCommand(_executablePath), StringComparison.OrdinalIgnoreCase);
        return new StartupRegistrationStatus(
            enabled,
            canEnable,
            canEnable ? null : "development_build");
    }

    public StartupRegistrationStatus SetEnabled(bool enabled)
    {
        var status = GetStatus();
        if (enabled && !status.CanEnable)
        {
            return status;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, BuildCommand(_executablePath), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        return GetStatus();
    }

    private static bool IsPublishedLocation(string executablePath)
    {
        var normalized = executablePath.Replace('/', '\\');
        return !normalized.Contains("\\bin\\Debug\\", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("\\bin\\Release\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCommand(string executablePath) => $"\"{executablePath}\" --start-hidden";
}
