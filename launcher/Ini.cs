using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MihonWindows;

/// <summary>Minimal key=value file. Lines starting with # or ; are comments.</summary>
sealed class Ini
{
    readonly string path;
    readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

    public Ini(string path)
    {
        this.path = path;
        if (!File.Exists(path)) return;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            var eq = line.IndexOf('=');
            if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
    }

    public string Get(string key, string fallback = null) =>
        values.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;

    public int GetInt(string key, int fallback, int min, int max) =>
        int.TryParse(Get(key), out var value) ? Math.Max(min, Math.Min(max, value)) : fallback;

    public bool GetBool(string key, bool fallback) => Get(key)?.ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => fallback,
    };

    public void Set(string key, string value) => values[key] = value;

    public void Remove(string key) => values.Remove(key);

    public void RemoveWhere(Func<string, bool> match)
    {
        foreach (var key in values.Keys.Where(match).ToList()) values.Remove(key);
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllLines(path, values.Select(kv => kv.Key + "=" + kv.Value));
    }
}

/// <summary>Settings people may edit by hand in settings.ini. The file is created with explanations.</summary>
sealed class Settings
{
    public int RamMb { get; private set; }
    public int CpuCores { get; private set; }
    public int StorageGb { get; private set; }
    public string Dpi { get; private set; }
    public string VideoBitRate { get; private set; }
    public string Gpu { get; private set; }
    public string Keyboard { get; private set; }
    public string ExtensionInstaller { get; private set; }
    public string View { get; private set; }
    public bool TrimAndroid { get; private set; }
    public string Animations { get; private set; }
    public bool AutoUpdate { get; private set; }
    public string ReleaseRepo { get; private set; }
    public string ReleaseAsset { get; private set; }
    public bool KeepRunning { get; private set; }
    public bool CopyBackups { get; private set; }

    public static Settings Load()
    {
        if (!File.Exists(Paths.SettingsFile))
        {
            Directory.CreateDirectory(Paths.Root);
            File.WriteAllText(Paths.SettingsFile, Template);
        }
        var ini = new Ini(Paths.SettingsFile);
        var written = File.ReadAllText(Paths.SettingsFile);
        if (TemplateKeys().Any(key => ini.Get(key) == null) || OlderDefaultExplanations.Any(written.Contains))
        {
            // Written by an older version: rewrite it with the current explanations, keeping the values.
            Upgrade(ini, written);
            ini = new Ini(Paths.SettingsFile);
        }
        return new Settings
        {
            RamMb = ini.GetInt("ram_mb", 1536, 1536, 16384),
            CpuCores = ini.GetInt("cpu_cores", DefaultCores, 1, MaxCores),
            StorageGb = ini.GetInt("storage_gb", 16, 6, 512),
            Dpi = ini.Get("dpi", "auto"),
            VideoBitRate = ini.Get("video_bit_rate", "8M"),
            Gpu = ini.Get("gpu", "auto").ToLowerInvariant(),
            Keyboard = ini.Get("keyboard", "uhid").ToLowerInvariant() == "sdk" ? "sdk" : "uhid",
            ExtensionInstaller = ini.Get("extension_installer", "private").ToLowerInvariant() == "mihon" ? "mihon" : "private",
            View = ini.Get("view", "emulator").ToLowerInvariant() == "scrcpy" ? "scrcpy" : "emulator",
            TrimAndroid = ini.GetBool("trim_android", true),
            Animations = ini.Get("animations", "off").ToLowerInvariant() switch
            {
                "normal" => "normal",
                "fast" => "fast",
                _ => "off",
            },
            AutoUpdate = ini.GetBool("auto_update", true),
            ReleaseRepo = ini.Get("release_repo", "mihonapp/mihon"),
            ReleaseAsset = ini.Get("release_asset", "mihon-x86_64-v*.apk"),
            KeepRunning = ini.GetBool("keep_running", false),
            CopyBackups = ini.GetBool("copy_backups", true),
        };
    }

    /// <summary>Changes one value in settings.ini and keeps the comments.</summary>
    public static void Write(string key, string value)
    {
        Load();
        var lines = File.ReadAllLines(Paths.SettingsFile).ToList();
        var index = lines.FindIndex(l => l.TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
        if (index >= 0) lines[index] = key + "=" + value;
        else lines.Add(key + "=" + value);
        File.WriteAllLines(Paths.SettingsFile, lines);
    }

    static IEnumerable<string> TemplateLines() => Template.Split('\n').Select(line => line.TrimEnd('\r'));

    static IEnumerable<string> TemplateKeys() =>
        TemplateLines().Where(line => !line.StartsWith("#") && line.IndexOf('=') > 0).Select(line => line.Substring(0, line.IndexOf('=')));

    static void Upgrade(Ini current, string written)
    {
        File.WriteAllLines(Paths.SettingsFile, TemplateLines().Select(line =>
        {
            var eq = line.IndexOf('=');
            if (line.StartsWith("#") || eq <= 0) return line;
            var key = line.Substring(0, eq);
            return current.Get(key) is string value ? key + "=" + Migrate(key, value, current, written) : line;
        }));
    }

    // How settings.ini explained defaults that have changed since. A file that still says so was written with them.
    const string FastAnimationsExplanation = "fast plays them at double speed, so moving around Mihon feels quicker";
    const string FourCoresExplanation = "4 cores keep Mihon smooth";
    static readonly string[] OlderDefaultExplanations = { FastAnimationsExplanation, FourCoresExplanation };

    /// <summary>Replaces values that were defaults in older versions with the current ones.</summary>
    static string Migrate(string key, string value, Ini current, string written) => key switch
    {
        // fast was the default until it turned out that a turning loading spinner costs as much CPU as scrolling.
        "animations" when value == "fast" && written.Contains(FastAnimationsExplanation) => "off",
        // Half of the PC's processors, up to 4, was the default until 2 turned out to be as smooth with less CPU.
        "cpu_cores" when written.Contains(FourCoresExplanation) &&
                         value == Math.Max(2, Math.Min(4, Environment.ProcessorCount / 2)).ToString() => DefaultCores.ToString(),
        // 3072 was the default before Android got trimmed, and 2048 before the window drew straight from the emulator
        // (view=); now it gets by with 1.5 GB.
        "ram_mb" when (value == "3072" || value == "2048") && current.Get("view") == null => "1536",
        // Before the window drew straight from the emulator (view=), Android also compressed the window's picture, and
        // the default was half of the PC's processors, up to 6 (8 even earlier).
        "cpu_cores" when current.Get("view") == null && int.TryParse(value, out var cores) &&
                         (cores > MaxCores || cores == Math.Max(2, Math.Min(MaxCores, Environment.ProcessorCount / 2))) => DefaultCores.ToString(),
        "cpu_cores" when int.TryParse(value, out var cores) && cores > MaxCores => MaxCores.ToString(),
        _ => value,
    };

    // The emulator refuses more than 6 cores ("Number of cores set to 6").
    const int MaxCores = 6;

    // On an 8-core/16-thread laptop, 2 cores scrolled and browsed with the same frame times as 3 or 4, while 4 took
    // about a quarter more CPU scrolling and a third more browsing: each extra core adds the emulator's work of waking
    // it and of signals between cores, not speed.
    const int DefaultCores = 2;

    static string Template => $$"""
        # Mihon Desktop App settings. Close Mihon, edit this file, then open Mihon again.
        # Delete a line (or the whole file) to go back to the default.

        # Memory in MB and CPU cores for the Android device. 1.5 GB is plenty once Android is trimmed
        # (trim_android below), and 2 cores scroll as smoothly as more: each extra core (the emulator
        # takes up to 6) only adds the emulator's own work and takes more from your PC. 1 core takes
        # less CPU still while scrolling, but Mihon starts about half again slower and screens hitch more.
        ram_mb=1536
        cpu_cores={{DefaultCores}}

        # Android storage in GB. Only used when the Android device is first created.
        storage_gb=16

        # How the Mihon window gets its picture. emulator: straight from the emulator's graphics, which
        # runs at 60 fps and takes about half the CPU while scrolling. scrcpy: as a compressed video
        # stream (the older way, used automatically if the emulator's picture isn't available).
        view=emulator

        # Size of everything in the Mihon window. "auto" follows your Windows display scaling.
        # 160 = 100%, 200 = 125%, 240 = 150%.
        dpi=auto

        # Video quality of the Mihon window with view=scrcpy, in bits per second. Still pages are sharp
        # either way; higher values only help while scrolling and cost CPU (24M made scrolling choppy).
        video_bit_rate=8M

        # Graphics. auto draws Android with your graphics card and falls back to software drawing
        # if that doesn't work. host = graphics card only, software = slow but most compatible.
        gpu=auto

        # Keyboard. uhid acts like a real (US) keyboard plugged into Android, so keys type the same
        # whatever Windows layout is active. sdk sends the characters Windows produces instead, but
        # can't type non-Latin letters. Either way, paste (Ctrl+V) works for text in any language.
        keyboard=uhid

        # Extensions. private: Mihon installs them inside itself, with no Android "Install this app?"
        # prompt (that prompt can open hidden behind the window if you click while it appears, which
        # leaves extensions stuck on "Installing"). mihon: use the installer picked in Mihon's own
        # settings (More > Settings > Advanced > Extension installer).
        extension_installer=private

        # Switch off the parts of Android that Mihon doesn't use: Google's apps and Play services,
        # Bluetooth, and the emulator's unused hardware. Saves about 1 GB of memory and keeps Android
        # quiet in the background. false switches Android's apps back on.
        trim_android=true

        # Android's animations. off: screens and pictures appear straight away and loading spinners stand
        # still. That saves a lot of CPU: a turning spinner costs as much as scrolling, and moving around
        # Mihon takes about a third less. Scrolling is just as smooth either way.
        # fast: animations at double speed. normal: as on a phone.
        animations=off

        # Check GitHub for a newer Mihon release every time Mihon starts, and install it.
        auto_update=true
        release_repo=mihonapp/mihon
        release_asset=mihon-x86_64-v*.apk

        # Keep Android running after you close the window. Reopening is instant and library
        # updates keep running, but Android keeps using about 1.5 GB of memory.
        keep_running=false

        # Copy Mihon's automatic backups to Documents\Mihon for Windows\Backups when you close Mihon.
        copy_backups=true

        """;
}

/// <summary>Things the launcher remembers between runs (state.ini).</summary>
sealed class State
{
    readonly Ini ini = new(Paths.StateFile);

    public string Get(string key) => ini.Get(key);

    public void Set(string key, string value)
    {
        ini.Set(key, value);
        ini.Save();
    }

    public void Remove(string key)
    {
        ini.Remove(key);
        ini.Save();
    }
}
