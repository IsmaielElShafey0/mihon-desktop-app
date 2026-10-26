using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MihonWindows;

/// <summary>
/// scrcpy shows Mihon in its own window: Android creates a virtual display for Mihon alone, and
/// --flex-display resizes that display with the window, so Mihon lays itself out like a desktop app.
/// </summary>
static class Scrcpy
{
    public const string Version = "5.0.1";
    const string Url = "https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-win64-v5.0.1.zip";
    const string Digest = "sha256:b12a2c4ee8be317422451fc7dcf8ee20a71b5ea7ef9ad73ddd825a25316227a5";
    const long Size = 11264634;

    public static bool IsInstalled(State state) => File.Exists(Paths.Scrcpy) && state.Get("scrcpy") == Version;

    public static async Task InstallAsync(State state, Action<string, double?> report, CancellationToken ct)
    {
        var zip = Path.Combine(Paths.Downloads, Path.GetFileName(Url));
        await Net.DownloadAsync(Url, zip, Size, Digest, (done, total) => report("Downloading scrcpy…", (double)done / total), ct);
        report("Unpacking scrcpy…", null);
        Net.ExtractZip(zip, Paths.ScrcpyDir, null, ct);
        File.Delete(zip);

        // scrcpy reads its window icon from SCRCPY_ICON_DIR.
        Directory.CreateDirectory(Paths.IconDir);
        using (var icon = Resources.Open("Mihon.png"))
        using (var file = File.Create(Path.Combine(Paths.IconDir, "scrcpy.png")))
            icon.CopyTo(file);
        File.Copy(Path.Combine(Paths.ScrcpyDir, "disconnected.png"), Path.Combine(Paths.IconDir, "disconnected.png"), true);
        state.Set("scrcpy", Version);
    }

    public static ScrcpySession Start(Settings settings, WindowBounds bounds)
    {
        var args = new List<string>
        {
            "--serial=" + Device.Serial,
            $"--new-display={bounds.Width}x{bounds.Height}/{AndroidDpi(settings)}",
            "--flex-display",
            "--start-app=" + MihonApp.Package,
            "--keep-active",
            "--no-vd-system-decorations",
            "--no-vd-destroy-content",   // closing the window parks Mihon on the hidden main screen instead of killing it
            "--no-audio",
            // uhid: Android sees a real keyboard, so typing doesn't depend on the Windows layout
            // (the default sdk mode can only type Latin characters).
            "--keyboard=" + settings.Keyboard,
            "--display-ime-policy=local",
            "--window-title=Mihon",
            "--video-bit-rate=" + settings.VideoBitRate,
            "--mouse-bind=b-b-:++++",    // right-click and the mouse's back button go back; Shift+click sends them to Mihon
            "--shortcut-mod=rctrl",      // scrcpy's own shortcuts (Right Ctrl+key) stay out of the way
        };
        // With --flex-display the window takes its size from --new-display above.
        if (bounds.HasPosition)
        {
            args.Add("--window-x=" + bounds.X);
            args.Add("--window-y=" + bounds.Y);
        }

        var psi = Proc.StartInfo(Paths.Scrcpy, args);
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = Encoding.UTF8;
        var log = new StreamWriter(
            new FileStream(Log.ScrcpyLog, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
            new UTF8Encoding(false)) { AutoFlush = true };
        var session = new ScrcpySession(new Process { StartInfo = psi, EnableRaisingEvents = true });
        void Write(string line)
        {
            if (line == null) return;
            session.OnOutput(line);
            lock (log)
            {
                try { log.WriteLine(line); }
                catch (ObjectDisposedException) { }
            }
        }

        var process = session.Process;
        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.Exited += (_, _) => Task.Delay(2000).ContinueWith(_ => { lock (log) log.Dispose(); });
        Log.Info("Starting scrcpy: " + psi.Arguments);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return session;
    }

    /// <summary>Android's 160 dpi matches Windows at 100% scaling, so "auto" follows the Windows scale.</summary>
    public static int AndroidDpi(Settings settings) =>
        int.TryParse(settings.Dpi, out var dpi) && dpi >= 80 && dpi <= 640 ? dpi : (int)Math.Round(160 * Native.GetDpiForSystem() / 96.0);

    public static async Task<IntPtr> WaitForWindowAsync(Process process, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (process.HasExited) return IntPtr.Zero;
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle;
            await Task.Delay(150, ct);
        }
        return IntPtr.Zero;
    }

    /// <summary>Waits for the window to close, remembering where it was so it reopens in the same place.</summary>
    public static async Task<WindowBounds> TrackUntilClosedAsync(Process process, IntPtr window, WindowBounds bounds)
    {
        var exited = Proc.WaitForExitAsync(process);
        while (await Task.WhenAny(exited, Task.Delay(1000)) != exited)
            bounds = WindowBounds.FromWindow(window) ?? bounds;
        return bounds;
    }

    /// <summary>Brings an already open Mihon window to the front.</summary>
    public static bool ActivateRunningWindow()
    {
        foreach (var process in Process.GetProcessesByName("scrcpy"))
        {
            using (process)
            {
                try
                {
                    if (!process.MainModule.FileName.StartsWith(Paths.ScrcpyDir, StringComparison.OrdinalIgnoreCase)) continue;
                    var window = process.MainWindowHandle;
                    if (window == IntPtr.Zero) continue;
                    if (Native.IsIconic(window)) Native.ShowWindow(window, Native.SW_RESTORE);
                    Native.SetForegroundWindow(window);
                    return true;
                }
                catch
                {
                    // Not ours, or gone.
                }
            }
        }
        return false;
    }
}

/// <summary>A running scrcpy window, and the Android display it created for Mihon.</summary>
sealed class ScrcpySession : IDisposable
{
    static readonly Regex NewDisplay = new(@"New display: \S+ \(id=(\d+)\)");
    readonly TaskCompletionSource<int> displayId = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ScrcpySession(Process process) => Process = process;

    public Process Process { get; }

    /// <summary>Completes once scrcpy reports the display it made for Mihon.</summary>
    public Task<int> DisplayId => displayId.Task;

    public void OnOutput(string line)
    {
        var match = NewDisplay.Match(line);
        if (match.Success) displayId.TrySetResult(int.Parse(match.Groups[1].Value));
    }

    public void Dispose() => Process.Dispose();
}

/// <summary>Client-area position and size of the Mihon window, in physical pixels.</summary>
sealed class WindowBounds
{
    public int X, Y, Width, Height;
    public bool HasPosition;

    public override string ToString() => $"{X},{Y},{Width},{Height}";

    public static WindowBounds Load(State state)
    {
        var parts = (state.Get("window") ?? "").Split(',');
        if (parts.Length == 4 && parts.All(p => int.TryParse(p, out _)))
        {
            var saved = new WindowBounds
            {
                X = int.Parse(parts[0]), Y = int.Parse(parts[1]), Width = int.Parse(parts[2]), Height = int.Parse(parts[3]), HasPosition = true,
            };
            // Only reuse it if the title bar would still be on a connected screen.
            var titleBar = new Rectangle(saved.X, saved.Y - 20, Math.Max(100, saved.Width), 20);
            if (saved.Width >= 320 && saved.Height >= 240 && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(titleBar)))
                return saved;
        }
        var area = Screen.PrimaryScreen.WorkingArea;
        var scale = Native.GetDpiForSystem() / 96.0;
        return new WindowBounds
        {
            Width = (int)Math.Min(1100 * scale, area.Width * 0.85),
            Height = (int)Math.Min(760 * scale, area.Height * 0.85),
        };
    }

    public static WindowBounds FromWindow(IntPtr window)
    {
        if (!Native.IsWindow(window) || Native.IsIconic(window) || Native.IsZoomed(window)) return null;
        if (!Native.GetClientRect(window, out var client)) return null;
        var origin = new Native.Point();
        if (!Native.ClientToScreen(window, ref origin)) return null;
        return new WindowBounds { X = origin.X, Y = origin.Y, Width = client.Right, Height = client.Bottom, HasPosition = true };
    }
}
