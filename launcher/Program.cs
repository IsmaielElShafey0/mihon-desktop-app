using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace MihonWindows;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Native.SetCurrentProcessExplicitAppUserModelID(AppInfo.AppUserModelId);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Log.Open();
        Log.Info($"{AppInfo.Name} {AppInfo.Version} ({Application.ExecutablePath}) {string.Join(" ", args)}");
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        using var instance = new SingleInstance();
        if (Has("--uninstall"))
        {
            if (instance.TryAcquire()) Installer.Uninstall();
            else MessageBox.Show("Close Mihon first, then try again.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // A newer downloaded copy goes on to offer the update (once any open Mihon has closed).
        var installed = Installer.InstalledVersion;
        var isUpdate = !Installer.IsRunningInstalledCopy && (installed == null || installed < AppInfo.AssemblyVersion);
        if (!isUpdate)
        {
            // Opening Mihon while it's already open just brings its window forward.
            if (!instance.TryAcquire() && ActivateRunningWindow()) return;

            // A downloaded copy that isn't newer than the installed one simply opens the installed one.
            if (!Installer.IsRunningInstalledCopy)
            {
                instance.Dispose();
                Process.Start(Paths.InstalledExe, string.Join(" ", args.Select(Proc.Quote)))?.Dispose();
                return;
            }
        }

        Application.Run(new LauncherForm(instance, Has("--cold-boot"), Has("--reset-android")));
    }

    /// <summary>
    /// Brings the open Mihon forward: the other launcher's visible window (Mihon's own window, or its progress while
    /// it starts), or scrcpy's window.
    /// </summary>
    static bool ActivateRunningWindow()
    {
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(self.ProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.Id == self.Id || process.MainModule.FileVersionInfo.ProductName != AppInfo.Name) continue;
                    var window = process.MainWindowHandle;
                    if (window == IntPtr.Zero) continue;
                    if (Native.IsIconic(window)) Native.ShowWindow(window, Native.SW_RESTORE);
                    Native.SetForegroundWindow(window);
                    return true;
                }
                catch
                {
                    // Gone, or not ours.
                }
            }
        }
        return Scrcpy.ActivateRunningWindow();
    }
}

/// <summary>One Mihon at a time. The mutex is owned by the UI thread, which also releases it.</summary>
sealed class SingleInstance : IDisposable
{
    readonly Mutex mutex = new(false, @"Local\MihonForWindows");
    bool disposed;

    public bool Owned { get; private set; }

    public bool TryAcquire()
    {
        if (Owned) return true;
        try
        {
            Owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            Owned = true; // the previous owner crashed; the mutex is ours now
        }
        return Owned;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Owned) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
