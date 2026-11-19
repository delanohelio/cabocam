using System;
using System.IO;
using System.Text;

namespace CaboCam.Services;

/// <summary>Tiny file logger: %LocalAppData%\CaboCam\logs\cabocam-yyyyMMdd.log</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _dir;

    public static string Directory
    {
        get
        {
            if (_dir is not null) return _dir;
            _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CaboCam", "logs");
            return _dir;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, $"cabocam-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never throw
        }
    }

    /// <summary>Deletes log files older than 14 days.</summary>
    public static void Cleanup()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            foreach (var f in System.IO.Directory.EnumerateFiles(Directory, "cabocam-*.log"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
            }
        }
        catch { /* ignore */ }
    }
}
