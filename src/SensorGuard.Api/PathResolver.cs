namespace SensorGuard.Api;

/// <summary>
/// Resolves a configured file path: absolute paths and paths that exist relative to the working directory win;
/// otherwise the path is looked up next to the application (where the shipped data files are copied).
/// </summary>
public static class PathResolver
{
    public static string Resolve(string path) => Resolve(path, Directory.GetCurrentDirectory(), AppContext.BaseDirectory);

    public static string Resolve(string path, string workingDirectory, string applicationDirectory)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        if (File.Exists(Path.Combine(workingDirectory, path)))
        {
            return path;
        }

        var besideApplication = Path.Combine(applicationDirectory, path);
        return File.Exists(besideApplication) ? besideApplication : path;
    }
}
