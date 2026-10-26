using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MihonWindows;

sealed class MihonRelease
{
    public string Tag;
    public string Version;
    public string AssetName;
    public string Url;
    public long Size;
    public string Digest; // "sha256:…", published by GitHub for each release file
}

/// <summary>Keeps the Mihon app inside Android on the newest GitHub release.</summary>
static class MihonApp
{
    public const string Package = "app.mihon";

    public static async Task<MihonRelease> GetLatestReleaseAsync(Settings settings, CancellationToken ct)
    {
        var release = (Dictionary<string, object>)await Net.GetJsonAsync($"https://api.github.com/repos/{settings.ReleaseRepo}/releases/latest", ct);
        var tag = (string)release["tag_name"];
        var pattern = new Regex("^" + Regex.Escape(settings.ReleaseAsset).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
        foreach (Dictionary<string, object> asset in (object[])release["assets"])
        {
            var name = (string)asset["name"];
            if (!pattern.IsMatch(name)) continue;
            return new MihonRelease
            {
                Tag = tag,
                Version = tag.TrimStart('v'),
                AssetName = name,
                Url = (string)asset["browser_download_url"],
                Size = Convert.ToInt64(asset["size"]),
                Digest = asset.TryGetValue("digest", out var digest) ? digest as string : null,
            };
        }
        throw new LauncherException($"Mihon {tag} on GitHub has no file matching \"{settings.ReleaseAsset}\".");
    }

    /// <summary>The installed versionName, or null when Mihon isn't installed.</summary>
    public static async Task<string> GetInstalledVersionAsync(CancellationToken ct)
    {
        var output = await Device.ShellAsync("dumpsys package " + Package, ct);
        var match = Regex.Match(output, @"versionName=(\S+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Whether Mihon has a task on the given Android display.</summary>
    public static async Task<bool> IsOnDisplayAsync(int displayId, CancellationToken ct) =>
        (await FindTaskAsync(ct))?.Display == displayId;

    /// <summary>The display Mihon's task is on and the task's id, or null when Mihon has no task.</summary>
    static async Task<(int Display, int Id)?> FindTaskAsync(CancellationToken ct)
    {
        var current = -1;
        foreach (var line in (await Device.ShellAsync("dumpsys activity activities", ct)).Split('\n'))
        {
            var header = Regex.Match(line, @"^Display #(\d+)");
            if (header.Success) current = int.Parse(header.Groups[1].Value);
            // A display's own tasks are listed right under its header, indented by two spaces.
            else if (line.IndexOf(":" + Package + " ", StringComparison.Ordinal) >= 0 &&
                     Regex.Match(line, @"^  \* Task\{\S+ #(\d+) ") is { Success: true } task)
                return (current, int.Parse(task.Groups[1].Value));
        }
        return null;
    }

    public static async Task StartOnDisplayAsync(int displayId, CancellationToken ct)
    {
        var resolved = (await Device.ShellAsync(
                $"cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER {Package}", ct))
            .Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(Package + "/", StringComparison.Ordinal));
        var component = resolved ?? Package + "/eu.kanade.tachiyomi.ui.main.MainActivity";
        // 0x10200000 = NEW_TASK | RESET_TASK_IF_NEEDED, what a launcher uses: brings back an existing task.
        var output = await Device.ShellAsync(
            $"am start --display {displayId} -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -f 0x10200000 -n {component}", ct);
        Log.Info($"Started {component} on display {displayId}: {output.Replace('\n', ' ')}");
    }

    /// <summary>Puts Mihon on the given display, where it was left if it's still running.</summary>
    public static async Task ShowOnDisplayAsync(int displayId, CancellationToken ct)
    {
        var task = await FindTaskAsync(ct);
        if (task?.Display == displayId) return;
        if (task is { } running)
        {
            // Starting a running Mihon brings it back on the display it was on (say, the hidden main one after scrcpy
            // showed it). Moving its task keeps Mihon as it was, down to the page being read.
            await Device.ShellAsync($"am display move-stack {running.Id} {displayId}", ct);
            if (await IsOnDisplayAsync(displayId, ct))
            {
                Log.Info($"Moved Mihon from display {running.Display} to display {displayId}");
                return;
            }
        }
        await StartOnDisplayAsync(displayId, ct);
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(300, ct);
            if (await IsOnDisplayAsync(displayId, ct)) return;
        }
        Log.Info($"Restarting Mihon to move it to display {displayId}");
        Restarted = true;
        await Device.ShellAsync("am force-stop " + Package, ct);
        await StartOnDisplayAsync(displayId, ct);
    }

    /// <summary>
    /// Whether the launcher ended Mihon's process (to update it, say). The emulator then keeps the pictures that
    /// Mihon showed, and the quick-boot snapshot would bring them back into memory at every start.
    /// </summary>
    public static bool Restarted { get; private set; }

    /// <summary>
    /// Takes Mihon's splash screen away if Android left it covering Mihon, and says whether it had to. Now and then
    /// Android's window manager stops finishing the splash's closing animation: Mihon runs underneath a splash that
    /// never goes, and every later splash sticks the same way until Android restarts. The splash's picture belongs
    /// to SystemUI, so restarting SystemUI takes it away.
    /// </summary>
    public static async Task<bool> ClearStuckSplashAsync(CancellationToken ct)
    {
        // Mihon keeps its splash for at most 5 s, then fades it out in under half a second.
        foreach (var wait in new[] { 12, 3 })
        {
            await Task.Delay(TimeSpan.FromSeconds(wait), ct);
            var shown = await Device.ShellAsync($"dumpsys SurfaceFlinger | grep 'Output Layer' | grep -c 'Splash Screen {Package}'", ct);
            if (shown == "0") return false;
        }
        Log.Warn("Mihon's splash screen stayed on top of Mihon; restarting Android's SystemUI to take it away");
        SplashStuck = true;
        await Device.RunAsRootAsync("kill $(pidof com.android.systemui)", ct);
        return true;
    }

    /// <summary>Whether Android got stuck showing Mihon's splash; the quick-boot snapshot would keep it that way.</summary>
    public static bool SplashStuck { get; private set; }

    /// <summary>Installs APKs dropped on the window and copies other files to Android's Download folder.</summary>
    public static async Task ReceiveFilesAsync(string[] files)
    {
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                Log.Warn($"Not sending {file} to Android: it isn't a file");
                continue;
            }
            try
            {
                var apk = file.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
                var result = apk
                    ? await Device.AdbAsync(TimeSpan.FromMinutes(5), CancellationToken.None, "install", "-r", "-g", file)
                    : await Device.AdbAsync(TimeSpan.FromMinutes(10), CancellationToken.None, "push", file, "/sdcard/Download/");
                Log.Info($"{(apk ? "Installed" : "Copied")} {Path.GetFileName(file)}: {result.Output.Trim()}");
            }
            catch (Exception e)
            {
                Log.Warn($"Couldn't send {file} to Android: {e.Message}");
            }
        }
    }

    public static async Task<string> DownloadAsync(MihonRelease release, Action<long, long> progress, CancellationToken ct)
    {
        var apk = Path.Combine(Paths.Downloads, release.AssetName);
        await Net.DownloadAsync(release.Url, apk, release.Size, release.Digest, progress, ct);
        return apk;
    }

    public static async Task InstallAsync(string apk, CancellationToken ct)
    {
        // -r keeps Mihon's library and settings; -g grants its runtime permissions (notifications) up front.
        Restarted = true;
        var result = await Device.AdbAsync(TimeSpan.FromMinutes(5), ct, "install", "-r", "-g", apk);
        Log.Info("adb install: " + result.Output.Trim());
        if (result.Output.IndexOf("Success", StringComparison.Ordinal) < 0)
        {
            var reason = result.Output.IndexOf("INSTALL_FAILED_UPDATE_INCOMPATIBLE", StringComparison.Ordinal) >= 0
                ? " The Mihon inside Android was signed by someone else, so it can't be updated in place."
                : "";
            throw new LauncherException("Android couldn't install Mihon." + reason, result.Output.Trim());
        }
        // Lets Mihon install extensions without first sending you to an "Allow from this source" screen.
        await Device.ShellAsync($"appops set {Package} REQUEST_INSTALL_PACKAGES allow", ct);
        foreach (var old in Directory.GetFiles(Paths.Downloads, "*.apk").Where(f => !f.Equals(apk, StringComparison.OrdinalIgnoreCase)))
            File.Delete(old);
    }

    const string PreferencesFile = "/data/data/" + Package + "/shared_prefs/" + Package + "_preferences.xml";

    /// <summary>
    /// Makes Mihon install extensions with its private installer, which keeps them inside Mihon. Its default asks
    /// Android to install each one, and Android's "Install this app?" prompt ends up hidden behind the Mihon window
    /// when the window is clicked while the prompt opens. There's no Back or Recents button here to bring it back,
    /// so Mihon waits on it with every other extension stuck on "Installing". Release builds of Mihon leave this
    /// installer out of their settings screen, so it's written into Mihon's settings file as root, with Mihon stopped.
    /// </summary>
    public static async Task UsePrivateExtensionInstallerAsync(CancellationToken ct)
    {
        var status = await SetPrivateExtensionInstallerAsync(ct);
        // Anything but these means the script stopped Mihon to change its settings.
        if (status != "missing" && status != "already") Restarted = true;
        if (status == "missing")
        {
            // A newly installed Mihon writes its settings file when it first runs, so run it once out of sight.
            Restarted = true;
            await StartOnDisplayAsync(0, ct);
            for (var i = 0; i < 20 && await Device.RunAsRootAsync($"[ -f {PreferencesFile} ] && echo yes", ct) != "yes"; i++)
                await Task.Delay(1000, ct);
            await Task.Delay(1000, ct);
            await Device.ShellAsync("am force-stop " + Package, ct);
            status = await SetPrivateExtensionInstallerAsync(ct);
        }
        if (status == "set") Log.Info("Mihon now installs extensions privately");
        else if (status != "already") Log.Warn("Couldn't switch Mihon to its private extension installer: " + status);
    }

    static Task<string> SetPrivateExtensionInstallerAsync(CancellationToken ct) => Device.RunAsRootAsync($$"""
        f={{PreferencesFile}}
        t=/data/local/tmp/mihon_preferences.xml
        v='<string name="extension_installer">PRIVATE</string>'
        [ -f $f ] || { echo missing; exit; }
        grep -qF "$v" $f && { echo already; exit; }
        am force-stop {{Package}}
        [ -f $f.bak ] && { echo "Mihon was saving its settings"; exit; }
        if grep -q 'name="extension_installer"' $f
        then sed "s#<string name=\"extension_installer\">[^<]*</string>#$v#" $f > $t
        else sed "s#</map>#$v</map>#" $f > $t
        fi
        # Writing into the existing file keeps its owner and SELinux label.
        cat $t > $f && rm $t && echo set
        """, ct);

    /// <summary>Compares versions like 0.21.2. Anything after the numbers (-r123, -beta) is ignored.</summary>
    public static int CompareVersions(string a, string b)
    {
        static int[] Parse(string version) => Regex.Match(version ?? "", @"^v?(\d+(?:\.\d+)*)").Groups[1].Value
            .Split('.').Select(part => int.TryParse(part, out var n) ? n : 0).ToArray();
        var x = Parse(a);
        var y = Parse(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var order = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (order != 0) return order;
        }
        return 0;
    }

    /// <summary>
    /// Copies Mihon's automatic backups out of Android. Mihon keeps them in an "autobackup" folder
    /// inside the storage folder picked during setup; this finds the ones in internal storage's top level.
    /// </summary>
    public static async Task CopyBackupsAsync(CancellationToken ct)
    {
        var listing = await Device.ShellAsync("ls -d /sdcard/*/autobackup", ct);
        foreach (var folder in listing.Split('\n').Select(f => f.Trim()).Where(f => f.StartsWith("/sdcard/") && f.EndsWith("/autobackup")))
        {
            var target = Path.Combine(Paths.Backups, folder.Split('/')[2]);
            Directory.CreateDirectory(target);
            var result = await Device.AdbAsync(TimeSpan.FromMinutes(2), ct, "pull", "-a", folder + "/.", target);
            Log.Info($"Copied backups from {folder} to {target}: {result.Output.Trim()}");
        }
    }
}
