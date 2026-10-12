namespace Jellyfin.Plugin.CzAnimeTitulky.Util;

/// <summary>
/// Plugin's own human-readable history (plugin data folder/activity.log). Jellyfin's log level
/// is often set to errors only, so this is where "what was downloaded and how was it timed" lives.
/// </summary>
public static class ActivityLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Lock = new();

    private static string FilePath => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "activity.log");

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Move(path, path + ".1", true);
                }

                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Last <paramref name="lines"/> lines, newest last.</summary>
    public static IReadOnlyList<string> Tail(int lines)
    {
        lock (Lock)
        {
            var path = FilePath;
            return File.Exists(path) ? File.ReadLines(path).TakeLast(lines).ToList() : Array.Empty<string>();
        }
    }
}
