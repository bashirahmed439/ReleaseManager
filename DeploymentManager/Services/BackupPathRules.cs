namespace DeploymentManager.Services;

public static class BackupPathRules
{
    public static bool IsExcluded(string relativePath, IEnumerable<string> exclusions)
    {
        var normalizedPath = relativePath.Replace('/', '\\').Trim('\\');
        return exclusions.Any(exclusion =>
        {
            var normalizedExclusion = exclusion.Replace('/', '\\').Trim('\\');
            return normalizedExclusion.Length > 0
                && (normalizedPath.Equals(normalizedExclusion, StringComparison.OrdinalIgnoreCase)
                    || normalizedPath.StartsWith(normalizedExclusion + "\\", StringComparison.OrdinalIgnoreCase));
        });
    }
}