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

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ImmersiveSBS", "ImmersiveSBS.log");

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
