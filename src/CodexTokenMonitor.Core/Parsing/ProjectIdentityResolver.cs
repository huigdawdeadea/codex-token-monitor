namespace CodexTokenMonitor.Core.Parsing;

internal static class ProjectIdentityResolver
{
    public static (string? Path, string? Name) Resolve(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return (null, null);
        }

        try
        {
            var fullPath = Path.GetFullPath(workingDirectory.Trim());
            var directory = new DirectoryInfo(fullPath);
            for (var current = directory; current is not null; current = current.Parent)
            {
                var gitMarker = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
                {
                    return (current.FullName, ProjectName(current));
                }
            }

            return (directory.FullName, ProjectName(directory));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, null);
        }
    }

    private static string ProjectName(DirectoryInfo directory) =>
        string.IsNullOrWhiteSpace(directory.Name) ? directory.FullName : directory.Name;
}
