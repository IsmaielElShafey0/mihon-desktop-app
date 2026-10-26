using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Text;

namespace MihonWindows;

/// <summary>Plain-text log in %LOCALAPPDATA%\MihonWindows\logs\launcher.log.</summary>
static class Log
{
    static readonly object Gate = new();
    static StreamWriter writer;

    public static readonly string FilePath = Path.Combine(Paths.Logs, "launcher.log");
    public static readonly string EmulatorLog = Path.Combine(Paths.Logs, "emulator.log");
    public static readonly string ScrcpyLog = Path.Combine(Paths.Logs, "scrcpy.log");

    public static void Open()
    {
        try
        {
            Directory.CreateDirectory(Paths.Logs);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2_000_000)
            {
                var old = Path.Combine(Paths.Logs, "launcher.old.log");
                File.Delete(old);
                File.Move(FilePath, old);
            }
            // Append-only access makes Windows put every write at the current end of the file, so two
            // launchers (one closing, one starting) can share the log without overwriting each other.
            var stream = new FileStream(FilePath, FileMode.Append, FileSystemRights.AppendData,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
            // Logging is best effort; another instance may be rotating the file.
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            writer?.Dispose();
            writer = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception e = null) => Write("ERROR", e == null ? message : message + ": " + e);

    static void Write(string level, string message)
    {
        lock (Gate)
        {
            try { writer?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}"); }
            catch { }
        }
    }

    /// <summary>The last lines of a log file, for error details.</summary>
    public static string Tail(string path, int lines = 20)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().TrimEnd().Split('\n');
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)).Select(l => l.TrimEnd('\r')));
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>An error with a message meant for the person using Mihon, plus optional technical details.</summary>
sealed class LauncherException : Exception
{
    public LauncherException(string message, string details = null) : base(message) => Details = details;

    public string Details { get; }
}
