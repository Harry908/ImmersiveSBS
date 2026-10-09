// Ported from DesktopSbS: DesktopSbS/Model/Log.cs
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ImmersiveSBS;

/// <summary>
/// A small persistent log at %LOCALAPPDATA%\ImmersiveSBS\ImmersiveSBS.log, kept for the events
/// that matter when the glasses go wrong and no debugger is attached. Debug.WriteLine is
/// compiled out of Release builds. Any thread; never throws.
/// </summary>
public static class Log
{
    private const long maxBytes = 1024 * 1024;
    private static readonly object fileLock = new();

    /// <summary>
    /// The app's own log; anything else running this code (the test host) writes beside it, so
    /// tests never put lines into the app's log that look like the app still running.
    /// </summary>
    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImmersiveSBS",
            Path.GetFileNameWithoutExtension(Environment.ProcessPath) is "ImmersiveSBS" ? "ImmersiveSBS.log" : "ImmersiveSBS.tests.log");

    public static void Write(string message)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + ThreadName() + "] " + message;
        Debug.WriteLine(line);

        try
        {
            lock (fileLock)
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // One previous file is kept, so a long session cannot fill the disk.
                var info = new FileInfo(path);
                if (info.Exists && info.Length > maxBytes)
                {
                    string old = path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }

                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Logging must never take rendering down.
        }
    }

    private static string ThreadName()
    {
        string? name = Thread.CurrentThread.Name;
        return string.IsNullOrEmpty(name) ? "thread " + Environment.CurrentManagedThreadId : name;
    }
}
