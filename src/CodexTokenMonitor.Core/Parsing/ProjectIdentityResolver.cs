namespace CodexTokenMonitor.Core.Parsing;

internal sealed record ProjectIdentity(string? Path, string? Name, string Status);

internal static class ProjectIdentityResolver
{
    public static ProjectIdentity Resolve(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return new ProjectIdentity(null, null, "unidentified");
        }

        try
        {
            var fullPath = Path.GetFullPath(workingDirectory.Trim());
            var directory = new DirectoryInfo(fullPath);
            for (var current = directory; current is not null; current = current.Parent)
            {
                var gitMarker = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(gitMarker))
                {
                    return new ProjectIdentity(current.FullName, ProjectName(current), "git");
                }

                if (File.Exists(gitMarker))
                {
                    var commonRoot = TryResolveCommonRepositoryRoot(current, gitMarker);
                    return commonRoot is null
                        ? new ProjectIdentity(current.FullName, ProjectName(current), "pending")
                        : new ProjectIdentity(commonRoot.FullName, ProjectName(commonRoot), "git");
                }
            }

            return new ProjectIdentity(
                directory.FullName,
                ProjectName(directory),
                Directory.Exists(directory.FullName) ? "directory" : "pending");
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or
                UnauthorizedAccessException or IOException)
        {
            return new ProjectIdentity(null, null, "unidentified");
        }
    }

    private static DirectoryInfo? TryResolveCommonRepositoryRoot(DirectoryInfo workTree, string gitFile)
    {
        var gitLine = File.ReadLines(gitFile).FirstOrDefault()?.Trim();
        const string prefix = "gitdir:";
        if (gitLine is null || !gitLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var gitDirectoryValue = gitLine[prefix.Length..].Trim();
        if (gitDirectoryValue.Length == 0)
        {
            return null;
        }

        var gitDirectory = ResolvePath(workTree.FullName, gitDirectoryValue);
        if (!Directory.Exists(gitDirectory))
        {
            return null;
        }

        var commonDirectoryFile = Path.Combine(gitDirectory, "commondir");
        if (!File.Exists(commonDirectoryFile))
        {
            return workTree;
        }

        var commonDirectoryValue = File.ReadLines(commonDirectoryFile).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(commonDirectoryValue))
        {
            return null;
        }

        var commonDirectory = new DirectoryInfo(ResolvePath(gitDirectory, commonDirectoryValue));
        return commonDirectory.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)
            ? commonDirectory.Parent
            : null;
    }

    private static string ResolvePath(string basePath, string candidate) =>
        Path.GetFullPath(Path.IsPathFullyQualified(candidate) ? candidate : Path.Combine(basePath, candidate));

    private static string ProjectName(DirectoryInfo directory) =>
        string.IsNullOrWhiteSpace(directory.Name) ? directory.FullName : directory.Name;
}
