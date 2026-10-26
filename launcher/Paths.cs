using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MihonWindows;

static class AppInfo
{
    public const string Name = "Mihon for Windows";
    public const string AppUserModelId = "MihonForWindows.Mihon";

    public static readonly Version AssemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
    public static readonly string Version = AssemblyVersion.ToString(3);

    /// <summary>GitHub repository that publishes this launcher, e.g. "IsmaielElShafey0/mihon".</summary>
    public static readonly string Repository = Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "Repository")?.Value ?? "";
}

static class Paths
{
    static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // Everything the launcher downloads or creates lives under Root.
    public static readonly string Root = Path.Combine(LocalAppData, "MihonWindows");
    public static readonly string Sdk = Path.Combine(Root, "sdk");
    public static readonly string AvdHome = Path.Combine(Root, "avd");
    public static readonly string AvdDir = Path.Combine(AvdHome, "mihon.avd");
    public static readonly string AndroidUserHome = Path.Combine(Root, "android-home");
    public static readonly string ScrcpyDir = Path.Combine(Root, "scrcpy");
    public static readonly string IconDir = Path.Combine(Root, "icon");
    public static readonly string Downloads = Path.Combine(Root, "downloads");
    public static readonly string Logs = Path.Combine(Root, "logs");
    public static readonly string SettingsFile = Path.Combine(Root, "settings.ini");
    public static readonly string StateFile = Path.Combine(Root, "state.ini");

    public static readonly string Emulator = Path.Combine(Sdk, "emulator", "emulator.exe");
    public static readonly string Adb = Path.Combine(Sdk, "platform-tools", "adb.exe");
    public static readonly string Scrcpy = Path.Combine(ScrcpyDir, "scrcpy.exe");

    // Where the launcher installs itself (per user, no admin rights needed).
    public static readonly string InstallDir = Path.Combine(LocalAppData, "Programs", "Mihon for Windows");
    public static readonly string InstalledExe = Path.Combine(InstallDir, "Mihon.exe");

    public static readonly string Backups = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mihon for Windows", "Backups");
}

static class Resources
{
    public static Stream Open(string name) =>
        typeof(Resources).Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException(name);
}

static class Format
{
    public static string Size(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.0} GB"
        : $"{bytes / (double)(1 << 20):0} MB";
}
