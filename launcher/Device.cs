using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MihonWindows;

/// <summary>The headless Android emulator that runs Mihon, and the adb connection to it.</summary>
static class Device
{
    public const int ConsolePort = 5580;   // adb finds emulators on even console ports 5554-5584
    public const string Serial = "emulator-5580";
    public const int AdbServerPort = 5137; // a private adb server, so other Android tools on this PC can't interfere

    public static Task<ProcResult> AdbAsync(TimeSpan timeout, CancellationToken ct, params string[] args) =>
        Proc.RunAsync(Paths.Adb, new[] { "-s", Serial }.Concat(args), timeout, ct);

    public static async Task<string> ShellAsync(string command, CancellationToken ct, int timeoutSeconds = 30)
    {
        var result = await AdbAsync(TimeSpan.FromSeconds(timeoutSeconds), ct, "shell", command);
        return result.Output.Trim();
    }

    public static async Task<bool> IsBootedAsync(CancellationToken ct)
    {
        if (!IsRunning()) return false;
        var result = await AdbAsync(TimeSpan.FromSeconds(10), ct, "shell", "getprop sys.boot_completed");
        return result.Ok && result.Output.Trim() == "1";
    }

    public static bool IsRunning()
    {
        // Android is the qemu process. "emulator" processes start it, and also watch it while it shuts down: those
        // watchdogs wait 20 seconds to end it should it hang, even when it has long exited.
        var processes = EmulatorProcesses();
        var running = processes.Any(IsQemu);
        foreach (var process in processes) process.Dispose();
        return running;
    }

    static bool IsQemu(Process process) => process.ProcessName.StartsWith("qemu-system-", StringComparison.Ordinal);

    /// <summary>
    /// The emulator, or with <paramref name="netsim"/> its network/Bluetooth helper netsimd, which outlives the
    /// emulator by a few seconds while it shuts down.
    /// </summary>
    static List<Process> EmulatorProcesses(bool netsim = false)
    {
        var found = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if ((netsim ? name == "netsimd" : name == "emulator" || name.StartsWith("qemu-system-", StringComparison.Ordinal)) &&
                    process.MainModule.FileName.StartsWith(Paths.Sdk, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(process);
                    continue;
                }
            }
            catch
            {
                // Process we may not inspect, or one that just exited.
            }
            process.Dispose();
        }
        return found;
    }

    /// <summary>
    /// Emulated hardware Mihon doesn't use: protected VMs, the sound card (there's no audio anyway), Bluetooth radio
    /// emulation, and Vulkan (Android draws with OpenGL here, so the emulator needn't load the graphics driver's
    /// Vulkan side). Ultra-wideband stays on: without it, Android's UWB service asks for the missing chip every
    /// second, forever. Its device needs the console that Android's log mirror uses, so that stays too, and the log
    /// mirror is stopped inside Android instead (<see cref="StopLogMirrorAsync"/>).
    /// </summary>
    const string UnusedFeatures = "-AndroidVirtualizationFramework,-VirtioSndCard,-BluetoothEmulation,-Vulkan";

    /// <summary>
    /// Everything that defines the emulated hardware. A quick-boot snapshot only works on the hardware it was saved
    /// with: the emulator even resizes the memory back to the snapshot's, and a snapshot loaded with other features
    /// leaves Android unreachable. So a change means one cold boot.
    /// </summary>
    public static string HardwareSignature(Settings settings, string gpu) =>
        $"ram={settings.RamMb},cores={settings.CpuCores},gpu={gpu},features={UnusedFeatures},view={settings.View}";

    // Display flags for Mihon's display: public, trusted, own content only, touch, rotates with content. Unlike the
    // emulator's defaults, no system decorations, so Android puts no status bar or taskbar on it.
    const int MihonDisplayFlags = 1 | 8 | 64 | 128 | 1024;

    /// <summary>The emulator's gRPC port and its access token, from the file it writes about itself while running.</summary>
    public static async Task<(int Port, string Token)> GrpcEndpointAsync(CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "avd", "running");
        var timer = Stopwatch.StartNew();
        var withoutGrpc = 0;
        while (true)
        {
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder, "pid_*.ini"))
                {
                    try
                    {
                        // Files of emulators that crashed stay behind; only a running one counts.
                        var pid = Regex.Match(Path.GetFileName(file), @"^pid_(\d+)\.ini$").Groups[1].Value;
                        if (!int.TryParse(pid, out var id) || !IsEmulatorProcess(id)) continue;
                        var info = new Ini(file);
                        if (info.Get("port.serial") != ConsolePort.ToString()) continue;
                        if (int.TryParse(info.Get("grpc.port"), out var port) && info.Get("grpc.token") is string token)
                            return (port, token);
                        // Android kept running (keep_running) from a session with view=scrcpy.
                        if (++withoutGrpc > 4) throw new LauncherException("The running emulator was started without its control connection.");
                    }
                    catch (IOException)
                    {
                        // Being written right now.
                    }
                }
            }
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new LauncherException("The emulator didn't open its control connection.");
            await Task.Delay(500, ct);
        }
    }

    /// <summary>Makes sure Android's helper for emulator displays runs; it creates the display Mihon is shown on.</summary>
    public static Task EnableMultiDisplayAsync(CancellationToken ct) => ShellAsync(
        "pm enable com.android.emulator.multidisplay >/dev/null; am broadcast -a com.android.emulator.multidisplay.START " +
        "-n com.android.emulator.multidisplay/.MultiDisplayServiceReceiver >/dev/null", ct);

    /// <summary>Creates Mihon's display, or resizes it (same Android display, so Mihon just lays itself out again).</summary>
    public static Task<bool> SetMihonDisplayAsync(int width, int height, int dpi)
    {
        // The emulator refuses displays narrower or lower than 320 dp.
        var minimum = (int)Math.Ceiling(320 * dpi / 160.0);
        return SendConsoleCommandAsync(
            $"multidisplay add {EmulatorView.Display} {Math.Max(minimum, width)} {Math.Max(minimum, height)} {dpi} {MihonDisplayFlags}");
    }

    /// <summary>Removes Mihon's display, for when scrcpy shows Mihon instead.</summary>
    public static Task<bool> RemoveMihonDisplayAsync() => SendConsoleCommandAsync($"multidisplay del {EmulatorView.Display}", quiet: true);

    /// <summary>Android's id for Mihon's display, once Android has it.</summary>
    public static async Task<int> MihonDisplayIdAsync(CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var match = Regex.Match(await ShellAsync("dumpsys display", ct), "\"Emulator 2D Display\", displayId (\\d+)");
            if (match.Success) return int.Parse(match.Groups[1].Value);
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new LauncherException("Android didn't create Mihon's display.");
            await Task.Delay(300, ct);
        }
    }

    static bool IsEmulatorProcess(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.ProcessName.StartsWith("qemu-system-", StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A free TCP port on this PC for the emulator's gRPC endpoint.</summary>
    static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Starts the emulator without a window; its output goes to logs\emulator.log.</summary>
    public static void Start(Settings settings, string gpu, bool coldBoot)
    {
        var args = new List<string>
        {
            "-avd", AndroidSdk.AvdName, "-port", ConsolePort.ToString(),
            "-no-audio", "-no-boot-anim", "-no-metrics", "-skip-adb-auth",
            // A crash report dialog would wait for an answer nobody sees, holding up every later start.
            "-crash-report-mode", "disabled",
            "-gpu", gpu, "-memory", settings.RamMb.ToString(), "-cores", settings.CpuCores.ToString(),
            // Otherwise the emulator raises the memory to 4 GB, its minimum for this Android version, although a
            // trimmed Android with Mihon runs comfortably in 1.5 GB. (It doesn't put Android in low-RAM mode.)
            "-lowram",
            "-feature", UnusedFeatures,
        };
        if (settings.View == "emulator")
        {
            // The emulator's own window stays hidden; it's only there because extra displays (Mihon's) need it.
            // With a token, its gRPC endpoint listens on localhost only.
            args.AddRange(new[] { "-qt-hide-window", "-grpc", FreePort().ToString(), "-grpc-use-token" });
        }
        else
        {
            args.Add("-no-window");
        }
        if (coldBoot) args.Add("-no-snapshot-load");
        // A netsimd left over from the last session would take the new emulator down with it as it exits, and a
        // leftover shutdown watchdog (see IsRunning) could end the new emulator if Windows reused the old one's id.
        foreach (var process in EmulatorProcesses(netsim: true).Concat(EmulatorProcesses()))
        {
            Log.Warn($"Stopping a leftover {process.ProcessName}");
            Proc.TryKill(process);
            process.Dispose();
        }
        Log.Info("Starting the emulator: " + string.Join(" ", args));
        // The emulator expects its home folder to exist; without it, it retries creating a lock file there forever.
        Directory.CreateDirectory(Paths.AndroidUserHome);
        Directory.CreateDirectory(Paths.AvdHome);

        // cmd sends the output straight to a file, so the emulator doesn't depend on this process
        // staying alive to read it (keep_running leaves Android up after the launcher exits).
        var command = $"{Proc.Quote(Paths.Emulator)} {string.Join(" ", args.Select(Proc.Quote))} > {Proc.Quote(Log.EmulatorLog)} 2>&1";
        var psi = Proc.StartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Array.Empty<string>());
        psi.Arguments = $"/d /s /c \"{command}\"";
        psi.WorkingDirectory = Paths.Root;
        Process.Start(psi)?.Dispose();
    }

    public static async Task WaitForBootAsync(CancellationToken ct)
    {
        await Proc.RunAsync(Paths.Adb, new[] { "start-server" }, TimeSpan.FromSeconds(30), ct);
        var timer = Stopwatch.StartNew();
        while (!await IsBootedAsync(ct))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20) && !IsRunning())
                throw new LauncherException("The Android emulator closed while it was starting.", Log.Tail(Log.EmulatorLog));
            if (timer.Elapsed > TimeSpan.FromMinutes(8))
                throw new LauncherException("Android is taking too long to start.", Log.Tail(Log.EmulatorLog));
            // An emulator stuck in an error loop can write gigabytes of log; stop it before it fills the disk.
            if (File.Exists(Log.EmulatorLog) && new FileInfo(Log.EmulatorLog).Length > 50_000_000)
            {
                var details = Log.Tail(Log.EmulatorLog);
                await StopAsync();
                throw new LauncherException("The Android emulator kept reporting errors while starting.", details);
            }
            await Task.Delay(1000, ct);
        }
        // The package manager can lag a little behind boot_completed.
        while (!(await ShellAsync("pm path android", ct)).StartsWith("package:", StringComparison.Ordinal))
        {
            if (timer.Elapsed > TimeSpan.FromMinutes(9)) throw new LauncherException("Android started but isn't responding.");
            await Task.Delay(1000, ct);
        }
        Log.Info($"Android is ready after {timer.Elapsed.TotalSeconds:0} s");
    }

    /// <summary>
    /// Windows slows down processes that aren't behind the window in front (EcoQoS, coarser timers), mostly on battery
    /// and the Balanced power plan. The emulator has no window of its own, yet it's what draws the Mihon window.
    /// </summary>
    public static void KeepAtFullSpeed()
    {
        foreach (var process in EmulatorProcesses())
        {
            try
            {
                // Taking control of both throttles with their state bits clear means "never throttle".
                var state = new Native.PowerThrottlingState
                {
                    Version = 1,
                    ControlMask = Native.PowerThrottlingExecutionSpeed | Native.PowerThrottlingIgnoreTimerResolution,
                };
                if (!Native.SetProcessInformation(process.Handle, Native.ProcessPowerThrottling, ref state, Marshal.SizeOf(state)))
                    Log.Warn($"Couldn't turn off power throttling for {process.ProcessName}: error {Marshal.GetLastWin32Error()}");
            }
            catch (Exception e)
            {
                Log.Warn($"Couldn't turn off power throttling for {process.ProcessName}: {e.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>One-time settings so Android behaves like a desktop app host.</summary>
    public static async Task ProvisionAsync(CancellationToken ct)
    {
        foreach (var command in new[]
        {
            "settings put global stay_on_while_plugged_in 15",    // never sleep (the emulator is always "plugged in")
            "settings put system screen_off_timeout 2147483647",
            "settings put secure show_ime_with_hard_keyboard 0",  // type with the PC keyboard, no on-screen keyboard
        })
        {
            await ShellAsync(command, ct);
        }
    }

    /// <summary>
    /// Turns off Android's on-screen keyboard apps. Gboard can't show its keyboard on Mihon's display, and in
    /// that state it swallows physical key presses (whole words, or single letters) instead of passing them on.
    /// With no keyboard app, Android delivers keys from the PC keyboard straight to Mihon.
    /// Runs on every start because Android may re-enable its default keyboard after a cold boot.
    /// </summary>
    public static async Task DisableOnScreenKeyboardsAsync(CancellationToken ct)
    {
        foreach (var ime in (await ShellAsync("ime list -s", ct)).Split('\n').Select(l => l.Trim()).Where(l => l.IndexOf('/') > 0))
        {
            Log.Info("Disabling on-screen keyboard " + ime);
            await ShellAsync("ime disable " + ime, ct);
        }
    }

    // Apps and services of the Google APIs system image that Mihon never uses. The launcher (home screen) stays:
    // without it Settings shows a fallback home that checks for a launcher twice a second, forever. Chrome stays
    // too, for tracker sign-ins and "Open in browser"; Mihon's WebView is a separate app.
    static readonly string[] UnusedPackages =
    {
        "com.google.android.gms", "com.google.android.gsf", "com.google.android.googlequicksearchbox",
        "com.google.android.as", "com.google.android.as.oss", "com.google.android.inputmethod.latin",
        "com.google.android.tts", "com.google.android.apps.docs", "com.google.android.apps.photos",
        "com.google.android.apps.wellbeing", "com.google.android.calendar", "com.google.android.deskclock",
        "com.google.android.apps.wallpaper", "com.google.android.dialer", "com.google.android.contacts",
        "com.google.android.apps.messaging", "com.google.android.gm", "com.google.android.youtube",
        "com.google.android.apps.youtube.music", "com.google.android.apps.maps", "com.google.android.apps.bard",
        "com.google.android.apps.restore", "com.google.android.partnersetup", "com.google.android.apps.pulse",
        "com.google.android.apps.safetyhub", "com.google.android.marvin.talkback",
        "com.google.android.apps.accessibility.voiceaccess", "com.google.android.accessibility.switchaccess",
        "com.google.android.projection.gearhead", "com.google.android.feedback", "com.google.android.settings.intelligence",
        "com.google.android.configupdater", "com.google.android.apps.customization.pixel", "com.google.android.markup",
        "com.google.android.googlesdksetup", "com.google.android.rkpdapp", "com.google.android.cellbroadcastreceiver",
        "com.google.android.tag", "com.google.android.printservice.recommendation", "com.android.vending",
        "com.android.camera2", "com.android.musicfx", "com.android.printspooler", "com.android.bips", "com.android.egg",
        "com.android.dreams.basic", "com.android.wallpaper.livepicker", "com.android.wallpaper", "com.android.wallpapercropper",
        "com.android.traceur", "com.android.DeviceAsWebcam", "com.android.bluetoothmidiservice", "com.android.stk",
        "com.android.emergency", "com.android.dynsystem", "com.android.htmlviewer", "com.android.bookmarkprovider",
        "com.android.providers.partnerbookmarks", "com.android.providers.calendar", "com.android.se",
    };

    // Bump when UnusedPackages changes, so trimmed devices pick up the difference.
    const string TrimVersion = "1";

    /// <summary>
    /// Switches off (or with <paramref name="trim"/> false, back on) the parts of Android Mihon doesn't use. Disabled
    /// apps don't start or take memory; together with Bluetooth that frees about 1 GB and most background work.
    /// </summary>
    public static async Task TrimAsync(bool trim, State state, Action<string> report, CancellationToken ct)
    {
        if (trim == (state.Get("android_trimmed") == TrimVersion)) return;
        var packages = string.Join(" ", UnusedPackages);
        if (trim)
        {
            report("Making Android lighter…");
            await ShellAsync($"for p in {packages}; do pm disable-user --user 0 $p >/dev/null 2>&1; done; cmd bluetooth_manager disable", ct, 180);
            state.Set("android_trimmed", TrimVersion);
            Log.Info($"Switched off {UnusedPackages.Length} Android apps and Bluetooth");
        }
        else
        {
            await ShellAsync($"for p in {packages}; do pm enable $p >/dev/null 2>&1; done; cmd bluetooth_manager enable", ct, 180);
            state.Remove("android_trimmed");
            Log.Info("Switched Android's own apps back on");
        }
    }

    /// <summary>
    /// Stops Android's service that copies every line of its log to the emulator, where nothing reads it. A stopped
    /// service stays stopped, also in quick-boot snapshots, until Android boots from scratch.
    /// </summary>
    public static async Task StopLogMirrorAsync(CancellationToken ct)
    {
        if (await ShellAsync("getprop init.svc.goldfish-logcat", ct) is "stopped" or "") return;
        await RunAsRootAsync("setprop ctl.stop goldfish-logcat", ct);
        Log.Info("Stopped Android's log mirror");
    }

    /// <summary>Runs a shell script as root, which Google APIs system images allow.</summary>
    public static Task<string> RunAsRootAsync(string script, CancellationToken ct) => ShellAsync(
        $"echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n")))} | base64 -d | su 0 sh", ct);

    /// <summary>Applies the animations setting: fast plays Android's animations at double speed.</summary>
    public static Task SetAnimationsAsync(string animations, CancellationToken ct)
    {
        var scale = animations switch { "normal" => "1", "off" => "0", _ => "0.5" };
        return ShellAsync(string.Join("; ",
            new[] { "window_animation_scale", "transition_animation_scale", "animator_duration_scale" }
                .Select(key => $"settings put global {key} {scale}")), ct);
    }

    /// <summary>Shuts the emulator down. It saves a quick-boot snapshot first, so the next start takes seconds.</summary>
    public static async Task StopAsync()
    {
        if (IsRunning())
        {
            // Mihon's display stays, to be restored with the rest of Android (see AndroidSdk.WriteAvd).
            Log.Info("Stopping the emulator");
            var logStart = LogLength();
            if (!await SendConsoleCommandAsync("kill"))
                await AdbAsync(TimeSpan.FromSeconds(20), CancellationToken.None, "emu", "kill");
            var timer = Stopwatch.StartNew();
            TimeSpan? saved = null;
            while (IsRunning() && timer.Elapsed < TimeSpan.FromMinutes(2))
            {
                // The emulator saves its quick-boot snapshot within a second or two and exits. Should it hang after
                // that, there's nothing left to wait for.
                saved ??= SnapshotSavedSince(logStart) ? timer.Elapsed : null;
                if (saved is { } at && timer.Elapsed - at > TimeSpan.FromSeconds(1)) break;
                await Task.Delay(250);
            }
            // Left over: the shutdown watchdogs (see IsRunning), and qemu only if it hung.
            foreach (var process in EmulatorProcesses())
            {
                if (IsQemu(process) && saved == null) Log.Warn("Force-stopping " + process.ProcessName);
                Proc.TryKill(process);
                process.Dispose();
            }
            Log.Info($"Emulator stopped after {timer.Elapsed.TotalSeconds:0.0} s");
        }
        // netsimd exits on its own shortly after the emulator; wait so a quick restart doesn't connect to it.
        var netsim = Stopwatch.StartNew();
        while (EmulatorProcesses(netsim: true) is { Count: > 0 } helpers)
        {
            var late = netsim.Elapsed > TimeSpan.FromSeconds(30);
            foreach (var process in helpers)
            {
                if (late) Proc.TryKill(process);
                process.Dispose();
            }
            if (late) break;
            await Task.Delay(500);
        }
        await StopAdbServerAsync();
    }

    static long LogLength()
    {
        try { return new FileInfo(Log.EmulatorLog).Length; }
        catch (IOException) { return 0; }
    }

    /// <summary>Whether the emulator has written "Saving snapshot '…' took … ms" to its log after the given position.</summary>
    static bool SnapshotSavedSince(long position)
    {
        try
        {
            using var file = new FileStream(Log.EmulatorLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length <= position) return false;
            file.Position = position;
            var text = new StreamReader(file).ReadToEnd();
            return Regex.IsMatch(text, @"Saving snapshot '[^']*' took \d+ ms");
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static async Task StopAdbServerAsync()
    {
        if (File.Exists(Paths.Adb))
            await Proc.RunAsync(Paths.Adb, new[] { "kill-server" }, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Sends a command to the emulator's telnet-style console. The console asks for the token the
    /// emulator wrote to a file whose path it prints in its greeting.
    /// </summary>
    static async Task<bool> SendConsoleCommandAsync(string command, bool quiet = false)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", ConsolePort);
            if (await Task.WhenAny(connect, Task.Delay(3000)) != connect) return false;
            await connect;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

            var greeting = await ReadResponseAsync(reader);
            var tokenFile = Regex.Match(greeting, @"'([^']+emulator_console_auth_token)'");
            if (tokenFile.Success && File.Exists(tokenFile.Groups[1].Value))
            {
                await writer.WriteLineAsync("auth " + File.ReadAllText(tokenFile.Groups[1].Value).Trim());
                await ReadResponseAsync(reader);
            }
            await writer.WriteLineAsync(command);
            await ReadResponseAsync(reader);
            return true;
        }
        catch (Exception e)
        {
            if (!quiet) Log.Warn("Emulator console: " + e.Message);
            return false;
        }
    }

    static async Task<string> ReadResponseAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        while (true)
        {
            var read = reader.ReadLineAsync();
            if (await Task.WhenAny(read, Task.Delay(5000)) != read) throw new TimeoutException("No answer from the emulator console.");
            var line = await read ?? throw new IOException("The emulator console closed the connection.");
            text.AppendLine(line);
            if (line.StartsWith("OK", StringComparison.Ordinal)) return text.ToString();
            if (line.StartsWith("KO", StringComparison.Ordinal)) throw new IOException("Emulator console: " + line);
        }
    }
}
