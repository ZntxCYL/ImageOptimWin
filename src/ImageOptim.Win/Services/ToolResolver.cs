using System.IO;

namespace ImageOptim.Win.Services;

public static class ToolResolver
{
    /// <summary>Finds a file shipped inside the application tool directory, including JS assets.</summary>
    public static string? FindEmbeddedAsset(string relativePath, AppSettings settings)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Split('/', '\\').Contains(".."))
            return null;
        if (!string.IsNullOrWhiteSpace(settings.ToolsDirectory))
        {
            var userAsset = Path.Combine(settings.ToolsDirectory.Trim(), relativePath);
            if (File.Exists(userAsset)) return Path.GetFullPath(userAsset);
        }
        var bundledAsset = Path.Combine(AppContext.BaseDirectory, "tools", relativePath);
        return File.Exists(bundledAsset) ? bundledAsset : null;
    }

    public static string? Find(string executableName, AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ToolsDirectory))
        {
            var userPath = Path.Combine(settings.ToolsDirectory.Trim(), executableName);
            if (File.Exists(userPath)) return Path.GetFullPath(userPath);
        }
        var appPath = Path.Combine(AppContext.BaseDirectory, "tools", executableName);
        if (File.Exists(appPath)) return appPath;

        var searchPaths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var path in searchPaths)
        {
            try
            {
                var fullPath = Path.Combine(path.Trim('"'), executableName);
                if (File.Exists(fullPath)) return fullPath;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
