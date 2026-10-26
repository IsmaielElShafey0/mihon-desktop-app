using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MihonWindows;

/// <summary>
/// Per-user install: copies Mihon.exe to %LOCALAPPDATA%\Programs\Mihon for Windows, adds Start menu
/// (and optionally desktop) shortcuts, and registers it under Settings › Apps for uninstalling.
/// </summary>
static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MihonForWindows";
    static readonly string StartMenuShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Mihon.lnk");
    static readonly string DesktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Mihon.lnk");

    public static bool IsRunningInstalledCopy =>
        string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(Paths.InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static Version InstalledVersion =>
        File.Exists(Paths.InstalledExe) && Version.TryParse(FileVersionInfo.GetVersionInfo(Paths.InstalledExe).FileVersion, out var version) ? version : null;

    /// <summary>The exe that shortcuts and the taskbar should start.</summary>
    public static string LaunchPath => File.Exists(Paths.InstalledExe) ? Paths.InstalledExe : Application.ExecutablePath;

    public static void Install(bool desktopShortcut)
    {
        Directory.CreateDirectory(Paths.InstallDir);
        if (!IsRunningInstalledCopy) File.Copy(Application.ExecutablePath, Paths.InstalledExe, true);
        CreateShortcut(StartMenuShortcut);
        if (desktopShortcut) CreateShortcut(DesktopShortcut);

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppInfo.Name);
        key.SetValue("DisplayVersion", AppInfo.Version);
        key.SetValue("Publisher", "Mihon for Windows (unofficial)");
        key.SetValue("DisplayIcon", Paths.InstalledExe + ",0");
        key.SetValue("InstallLocation", Paths.InstallDir);
        key.SetValue("UninstallString", Proc.Quote(Paths.InstalledExe) + " --uninstall");
        if (AppInfo.Repository.Length > 0) key.SetValue("URLInfoAbout", "https://github.com/" + AppInfo.Repository);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        Log.Info($"Installed {AppInfo.Version} to {Paths.InstallDir}");
    }

    static void CreateShortcut(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(Paths.InstalledExe);
            link.SetWorkingDirectory(Paths.InstallDir);
            link.SetDescription("Read manga with Mihon");
            link.SetIconLocation(Paths.InstalledExe, 0);
            // The same AppUserModelID as the running windows, so pinning and grouping work on the taskbar.
            var store = (IPropertyStore)link;
            Taskbar.Set(store, PropertyKey.AppUserModelId, AppInfo.AppUserModelId);
            store.Commit();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    public static void Uninstall()
    {
        var answer = MessageBox.Show(
            "Remove Mihon for Windows from this PC?\n\n" +
            "Yes: remove the app and its Android device, including your Mihon library, downloads and settings.\n" +
            "No: remove the app but keep the Android device, so reinstalling picks up where you left off.\n\n" +
            "Tip: make a backup in Mihon first (More › Backup and restore).",
            "Uninstall " + AppInfo.Name, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Cancel) return;
        var removeData = answer == DialogResult.Yes;

        Task.Run(Device.StopAsync).GetAwaiter().GetResult();
        foreach (var shortcut in new[] { StartMenuShortcut, DesktopShortcut })
            if (File.Exists(shortcut)) File.Delete(shortcut);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        Log.Info("Uninstalled" + (removeData ? " with data" : ""));
        Log.Close();
        if (removeData) DeleteDirectory(Paths.Root);
        MessageBox.Show(AppInfo.Name + " was removed.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);

        // The running exe can't delete itself; a short-lived cmd removes the install folder after this process exits.
        if (Directory.Exists(Paths.InstallDir))
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                $"/d /c ping -n 4 127.0.0.1 > nul & rmdir /s /q \"{Paths.InstallDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            })?.Dispose();
        }
    }

    public static void DeleteDirectory(string path)
    {
        for (var attempt = 1; attempt <= 5 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(1000);
            }
        }
    }
}

static class Taskbar
{
    /// <summary>
    /// Puts the Mihon window (the launcher's own, or scrcpy's) under Mihon's taskbar identity, so it groups with the
    /// Mihon shortcut and "Pin to taskbar" pins Mihon.exe rather than scrcpy.
    /// </summary>
    public static void AdoptWindow(IntPtr window)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            var hr = Native.SHGetPropertyStoreForWindow(window, ref iid, out var store);
            if (hr != 0)
            {
                Log.Warn($"SHGetPropertyStoreForWindow failed: 0x{hr:X8}");
                return;
            }
            try
            {
                var exe = Installer.LaunchPath;
                Set(store, PropertyKey.RelaunchCommand, Proc.Quote(exe));
                Set(store, PropertyKey.RelaunchDisplayNameResource, "Mihon");
                Set(store, PropertyKey.RelaunchIconResource, exe + ",0");
                Set(store, PropertyKey.AppUserModelId, AppInfo.AppUserModelId);
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception e)
        {
            Log.Warn("Couldn't set the taskbar identity: " + e.Message);
        }
    }

    public static void Set(IPropertyStore store, PropertyKey key, string value)
    {
        var variant = PropVariant.FromString(value);
        try
        {
            store.SetValue(ref key, ref variant);
        }
        finally
        {
            variant.Free();
        }
    }
}
