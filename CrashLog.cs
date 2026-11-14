namespace LiteRec;

/// <summary>
/// Appends unexpected errors to %AppData%\LiteRec\crash.log. A process that dies from an exception
/// on a worker thread leaves no window and no message, so this file is the only trace of what happened.
/// </summary>
internal static class CrashLog
{
    private static readonly object Lock = new();

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteRec", "crash.log");

    public static void Write(string source, Exception? error)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                // Keep the file from growing without bound.
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    info.Delete();
                }

                File.AppendAllText(FilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source} (v{typeof(CrashLog).Assembly.GetName().Version}, Windows {Environment.OSVersion.Version})\n{error}\n\n");
            }
        }
        catch (Exception)
        {
            // Logging must never cause a second failure.
        }
    }
}
