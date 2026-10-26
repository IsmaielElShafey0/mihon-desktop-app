using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace MihonWindows;

sealed class SdkPackage
{
    public string Id;
    public Version Revision;
    public string Url;
    public long Size;
    public string Sha1;

    public override string ToString() => $"{Id} r{Revision}";
}

/// <summary>
/// Installs Google's Android Emulator, platform tools and a system image straight from Google's SDK
/// repository (the same files sdkmanager uses, without needing Java), and defines the virtual device.
/// </summary>
static class AndroidSdk
{
    const string RepositoryBase = "https://dl.google.com/android/repository/";
    const string GoogleApisBase = RepositoryBase + "sys-img/google_apis/";
    public const string AvdName = "mihon";

    // Newest first. Google APIs images include an up-to-date WebView, which Mihon needs for
    // Cloudflare-protected sources. 16 KB page-size (ps16k) images are skipped on purpose.
    static readonly string[] SystemImages =
    {
        "system-images;android-37.0;google_apis;x86_64",
        "system-images;android-36.1;google_apis;x86_64",
        "system-images;android-36;google_apis;x86_64",
        "system-images;android-35;google_apis;x86_64",
    };

    public static string SystemImageDir(string package) => Path.Combine(Paths.Sdk, package.Replace(';', Path.DirectorySeparatorChar));

    public static bool IsInstalled(State state) =>
        File.Exists(Paths.Emulator) && File.Exists(Paths.Adb) &&
        state.Get("system_image") is string image && File.Exists(Path.Combine(SystemImageDir(image), "system.img"));

    public static async Task InstallAsync(State state, Action<string, double?> report, CancellationToken ct)
    {
        if (!File.Exists(Paths.Emulator) || !File.Exists(Paths.Adb))
        {
            report("Looking up the Android Emulator…", null);
            var tools = await ReadRepositoryAsync(RepositoryBase + "repository2-3.xml", RepositoryBase, ct);
            if (!File.Exists(Paths.Emulator))
                await InstallPackageAsync(Newest(tools, "emulator"), Path.Combine(Paths.Sdk, "emulator"), "the Android Emulator", report, ct);
            if (!File.Exists(Paths.Adb))
                await InstallPackageAsync(Newest(tools, "platform-tools"), Path.Combine(Paths.Sdk, "platform-tools"), "Android platform tools", report, ct);
        }

        report("Checking hardware virtualization…", null);
        await CheckAccelerationAsync(ct);

        var image = state.Get("system_image");
        if (image == null || !File.Exists(Path.Combine(SystemImageDir(image), "system.img")))
        {
            report("Looking up the Android system image…", null);
            var images = await ReadRepositoryAsync(GoogleApisBase + "sys-img2-3.xml", GoogleApisBase, ct);
            var package = SystemImages.Select(id => Newest(images, id)).FirstOrDefault(p => p != null)
                ?? throw new LauncherException("Google's repository doesn't list a suitable Android system image right now.");
            await InstallPackageAsync(package, SystemImageDir(package.Id), "the Android system image", report, ct);
            state.Set("system_image", package.Id);
        }
    }

    static async Task InstallPackageAsync(SdkPackage package, string destination, string name, Action<string, double?> report, CancellationToken ct)
    {
        if (package == null) throw new LauncherException($"Google's repository doesn't list {name} for Windows right now.");
        Log.Info($"Installing {package} from {package.Url}");
        var zip = Path.Combine(Paths.Downloads, package.Url.Substring(package.Url.LastIndexOf('/') + 1));
        await Net.DownloadAsync(package.Url, zip, package.Size, "sha1:" + package.Sha1,
            (done, total) => report($"Downloading {name}… {Format.Size(done)} of {Format.Size(total)}", total > 0 ? (double)done / total : null), ct);
        report($"Unpacking {name}…", 0);
        Net.ExtractZip(zip, destination, fraction => report($"Unpacking {name}…", fraction), ct);
        File.Delete(zip);
    }

    /// <summary>Reads one of Google's SDK repository manifests, keeping stable Windows x64 archives.</summary>
    static async Task<List<SdkPackage>> ReadRepositoryAsync(string manifestUrl, string baseUrl, CancellationToken ct)
    {
        var xml = new XmlDocument();
        xml.LoadXml(await Net.GetStringAsync(manifestUrl, ct, timeoutSeconds: 60));

        static XmlNode Child(XmlNode node, string name) => node?.SelectSingleNode($"*[local-name()='{name}']");
        static int Number(XmlNode node, string name) => int.TryParse(Child(node, name)?.InnerText, out var n) ? n : 0;

        var channels = new Dictionary<string, string>();
        foreach (XmlElement channel in xml.SelectNodes("//*[local-name()='channel']"))
            channels[channel.GetAttribute("id")] = channel.InnerText.Trim();

        var packages = new List<SdkPackage>();
        foreach (XmlElement package in xml.SelectNodes("//*[local-name()='remotePackage']"))
        {
            var channelRef = (Child(package, "channelRef") as XmlElement)?.GetAttribute("ref");
            if (channelRef != null && channels.TryGetValue(channelRef, out var channelName) && channelName != "stable") continue;
            var revision = Child(package, "revision");
            foreach (XmlNode archive in package.SelectNodes("*[local-name()='archives']/*[local-name()='archive']"))
            {
                var os = Child(archive, "host-os")?.InnerText;
                var arch = Child(archive, "host-arch")?.InnerText;
                if ((os != null && os != "windows") || (arch != null && arch != "x64")) continue;
                var complete = Child(archive, "complete");
                var url = Child(complete, "url").InnerText.Trim();
                packages.Add(new SdkPackage
                {
                    Id = package.GetAttribute("path"),
                    Revision = new Version(Number(revision, "major"), Number(revision, "minor"), Number(revision, "micro")),
                    Url = url.StartsWith("https://", StringComparison.Ordinal) ? url : baseUrl + url,
                    Size = long.Parse(Child(complete, "size").InnerText.Trim()),
                    Sha1 = Child(complete, "checksum").InnerText.Trim(),
                });
            }
        }
        return packages;
    }

    static SdkPackage Newest(IEnumerable<SdkPackage> packages, string id) =>
        packages.Where(p => p.Id == id).OrderByDescending(p => p.Revision).FirstOrDefault();

    static async Task CheckAccelerationAsync(CancellationToken ct)
    {
        var result = await Proc.RunAsync(Paths.Emulator, new[] { "-accel-check" }, TimeSpan.FromSeconds(60), ct);
        var lines = result.Output.Split('\n').Select(l => l.Trim()).ToList();
        Log.Info("accel-check: " + string.Join(" | ", lines.Where(l => l.Length > 0)));
        var index = lines.IndexOf("accel:");
        if (index >= 0 && index + 1 < lines.Count && lines[index + 1] == "0") return;
        throw new LauncherException(
            "Android needs hardware virtualization, which isn't available on this PC right now.\n\n" +
            "Open \"Turn Windows features on or off\", tick \"Windows Hypervisor Platform\", restart the PC, then open Mihon again. " +
            "If it's already ticked, turn on virtualization (Intel VT-x or AMD-V) in the PC's BIOS/UEFI settings.",
            result.Output.Trim());
    }

    /// <summary>Writes the virtual device definition. Values that come from settings.ini are refreshed on every start.</summary>
    public static void WriteAvd(string image, Settings settings, string gpu)
    {
        Directory.CreateDirectory(Paths.AvdDir);
        File.WriteAllText(Path.Combine(Paths.AvdHome, AvdName + ".ini"),
            $"avd.ini.encoding=UTF-8\r\npath={Paths.AvdDir}\r\ntarget={image.Split(';')[1]}\r\n");

        var config = new Ini(Path.Combine(Paths.AvdDir, "config.ini"));
        if (config.Get("AvdId") == null) config.Set("disk.dataPartition.size", settings.StorageGb + "G");
        static bool IsDisplayKey(string key) => key.StartsWith("hw.display", StringComparison.OrdinalIgnoreCase) &&
                                                key.Length > 10 && char.IsDigit(key[10]);
        if (settings.View != "emulator")
        {
            // Only the launcher's own window uses an extra emulator display (Mihon's); scrcpy makes its own.
            config.RemoveWhere(IsDisplayKey);
        }
        else
        {
            // Mihon's display stays in the quick-boot snapshot: removing it at every shutdown and adding it at every
            // start left graphics memory behind in the emulator each time. The emulator records the display here
            // (and updates the record as the window is resized), but a snapshot only loads on the hardware its session
            // started with. So the display's record is set back to the snapshot's.
            var snapshot = new Ini(Path.Combine(Paths.AvdDir, "snapshots", "default_boot", "hardware.ini"));
            var saved = new[] { "width", "height", "density", "flag" }
                .Select(name => (Key: "hw.display1." + name, Value: snapshot.Get("hw.display1." + name))).ToList();
            if (saved.All(s => s.Value != null))
            {
                config.RemoveWhere(IsDisplayKey);
                foreach (var (key, value) in saved) config.Set(key, value);
            }
        }
        config.Set("AvdId", AvdName);
        config.Set("avd.ini.displayname", "Mihon");
        config.Set("avd.ini.encoding", "UTF-8");
        config.Set("PlayStore.enabled", "false");
        config.Set("abi.type", "x86_64");
        config.Set("hw.cpu.arch", "x86_64");
        config.Set("image.sysdir.1", image.Replace(';', '\\') + "\\");
        config.Set("tag.id", "google_apis");
        config.Set("tag.display", "Google APIs");
        config.Set("hw.ramSize", settings.RamMb.ToString());
        config.Set("hw.cpu.ncore", settings.CpuCores.ToString());
        config.Set("vm.heapSize", "512");
        // Mihon gets its own virtual display sized to its window, so the main screen is never seen.
        config.Set("hw.lcd.width", "1280");
        config.Set("hw.lcd.height", "800");
        config.Set("hw.lcd.density", "160");
        config.Set("hw.initialOrientation", "landscape");
        // With a "hardware" keyboard Android doesn't pop up the on-screen keyboard; typing comes from the PC.
        config.Set("hw.keyboard", "yes");
        config.Set("hw.mainKeys", "no");
        config.Set("hw.dPad", "no");
        config.Set("hw.trackBall", "no");
        config.Set("hw.gpu.enabled", "yes");
        config.Set("hw.gpu.mode", gpu);
        config.Set("hw.audioInput", "no");
        config.Set("hw.audioOutput", "no");
        config.Set("hw.camera.back", "none");
        config.Set("hw.camera.front", "none");
        config.Set("hw.gps", "no");
        // No sensors: nothing in a window needs them, and each one is another device for Android to poll.
        foreach (var sensor in new[]
        {
            "hw.accelerometer", "hw.accelerometer_uncalibrated", "hw.gyroscope", "hw.sensors.orientation",
            "hw.sensors.proximity", "hw.sensors.light", "hw.sensors.pressure", "hw.sensors.humidity",
            "hw.sensors.magnetic_field", "hw.sensors.magnetic_field_uncalibrated", "hw.sensors.gyroscope_uncalibrated",
            "hw.sensors.temperature", "hw.sensors.rgbclight", "hw.sensors.heart_rate", "hw.sensors.wrist_tilt",
            "hw.sensors.heading",
        })
        {
            config.Set(sensor, "no");
        }
        config.Set("hw.battery", "yes");
        config.Set("hw.sdCard", "no");
        config.Set("showDeviceFrame", "no");
        config.Set("fastboot.forceColdBoot", "no");
        config.Set("fastboot.forceFastBoot", "yes");
        config.Save();
    }
}
