namespace SensorGuard.Api;

/// <summary>
/// Resolves a configured file path: absolute paths and paths that exist relative to the working directory win;
/// otherwise the path is looked up next to the application (where the shipped data files are copied).
/// </summary>
public static class PathResolver
{
    public static string Resolve(string path)
    {
        if (Path.IsPathRooted(path) || File.Exists(path))
        {
            return path;
        }

        var besideApplication = Path.Combine(AppContext.BaseDirectory, path);
        return File.Exists(besideApplication) ? besideApplication : path;
    }
}
