using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MihonWindows;

/// <summary>
/// The small window shown while Mihon installs, starts Android or updates itself. It hides as soon as
/// the Mihon window opens, and the process then waits in the background to shut Android down afterwards.
/// </summary>
sealed class LauncherForm : Form
{
    readonly SingleInstance instance;
    readonly bool coldBoot;
    readonly bool resetAndroid;
    CancellationTokenSource cancel = new();
    TaskCompletionSource<bool> choice;
    bool busy;            // a flow is running; closing the window means "cancel"
    bool androidStarted;  // Android may be running and should be shut down when we stop early
    bool closing;

    readonly PictureBox logo = new();
    readonly Label title = new();
    readonly Label message = new();
    readonly ProgressBar progress = new();
    readonly TextBox details = new();
    readonly LinkLabel license = new();
    readonly LinkLabel logs = new();
    readonly CheckBox desktopShortcut = new();
    readonly CheckBox copyBackups = new();
    readonly Button primary = new();
    readonly Button secondary = new();

    public LauncherForm(SingleInstance instance, bool coldBoot, bool resetAndroid)
    {
        this.instance = instance;
        this.coldBoot = coldBoot;
        this.resetAndroid = resetAndroid;

        // Positions are in 96-dpi units and scaled by S(); fonts are in points, which Windows scales itself.
        // (WinForms' own auto-scaling on .NET Framework scaled some controls twice here.)
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Text = AppInfo.Name;
        Icon = new Icon(Resources.Open("Mihon.ico"));
        Font = new Font("Segoe UI", 9.75F);
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(S(560), S(328));

        logo.Image = Image.FromStream(Resources.Open("Mihon.png"));
        logo.SizeMode = PictureBoxSizeMode.Zoom;
        logo.Bounds = S(24, 24, 56, 56);

        title.Font = new Font("Segoe UI Semibold", 15F);
        title.AutoSize = true;
        title.Location = new Point(S(94), S(20));

        message.Bounds = S(97, 60, 440, 140);

        license.Text = "Read the Android SDK License Agreement";
        license.AutoSize = true;
        license.Location = new Point(S(97), S(200));
        license.LinkClicked += (_, _) => OpenExternal("https://developer.android.com/studio/terms");

        desktopShortcut.Text = "Create a desktop shortcut";
        desktopShortcut.Checked = true;
        desktopShortcut.AutoSize = true;
        desktopShortcut.Location = new Point(S(97), S(226));

        copyBackups.Text = @"Copy Mihon's automatic backups to Documents\Mihon for Windows";
        copyBackups.Checked = true;
        copyBackups.AutoSize = true;
        copyBackups.Location = new Point(S(97), S(250));

        progress.Bounds = S(97, 112, 440, 10);
        progress.Maximum = 1000;

        details.Bounds = S(97, 150, 440, 112);
        details.Multiline = true;
        details.ReadOnly = true;
        details.ScrollBars = ScrollBars.Vertical;
        details.BorderStyle = BorderStyle.None;
        details.BackColor = Color.FromArgb(244, 244, 244);
        details.Font = new Font("Consolas", 8.25F);

        logs.Text = "Open logs";
        logs.AutoSize = true;
        logs.Location = new Point(S(24), S(294));
        logs.LinkClicked += (_, _) => OpenExternal(Paths.Logs);

        primary.Bounds = S(306, 286, 138, 30);
        secondary.Bounds = S(452, 286, 88, 30);
        primary.Click += (_, _) => OnPrimary();
        secondary.Click += (_, _) => OnSecondary();

        Controls.AddRange(new Control[] { logo, title, message, license, desktopShortcut, copyBackups, progress, details, logs, primary, secondary });
        ResumeLayout(false);
        PerformLayout();
    }

    static readonly double DpiScale = Native.GetDpiForSystem() / 96.0;

    static int S(int value) => (int)Math.Round(value * DpiScale);

    static Rectangle S(int x, int y, int width, int height) => new(S(x), S(y), S(width), S(height));

    enum Page { Welcome, Progress, Error }

    Page page;

    void SetPage(Page next)
    {
        page = next;
        license.Visible = desktopShortcut.Visible = copyBackups.Visible = false;
        progress.Visible = next == Page.Progress;
        details.Visible = next == Page.Error && details.TextLength > 0;
        primary.Visible = next != Page.Progress;
        message.Height = S(next == Page.Progress ? 44 : next == Page.Error ? 84 : 140);
    }

    void ShowWelcome(bool update)
    {
        SetPage(Page.Welcome);
        title.Text = update ? "Update " + AppInfo.Name : AppInfo.Name;
        if (update)
        {
            message.Text = $"Version {AppInfo.Version} of {AppInfo.Name} is ready to install. Your Mihon library, downloads and settings stay as they are.";
            primary.Text = "Update";
        }
        else
        {
            message.Text =
                "Runs the official Mihon Android app in a private Android device on this PC, in its own window. " +
                "Each new Mihon release is installed automatically when you open it.\n\n" +
                "Setup downloads about 2.5 GB from Google and GitHub and needs about 13 GB of disk space. " +
                "Google's Android Emulator and system image are covered by the Android SDK License Agreement.";
            license.Visible = desktopShortcut.Visible = copyBackups.Visible = true;
            primary.Text = "Accept and install";
        }
        secondary.Text = "Cancel";
        AcceptButton = primary;
    }

    void ShowProgress(string status, double? fraction, bool cancellable = true)
    {
        if (page != Page.Progress) SetPage(Page.Progress);
        title.Text = "Mihon";
        message.Text = status;
        if (fraction is double value)
        {
            progress.Style = ProgressBarStyle.Continuous;
            progress.Value = (int)(Math.Max(0, Math.Min(1, value)) * 1000);
        }
        else if (progress.Style != ProgressBarStyle.Marquee)
        {
            progress.Style = ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = 30;
        }
        secondary.Text = "Cancel";
        secondary.Enabled = cancellable;
    }

    void ShowError(Exception error)
    {
        details.Text = error is LauncherException { Details: string text } ? text.Replace("\n", Environment.NewLine) : error.ToString();
        SetPage(Page.Error);
        title.Text = "Mihon couldn't start";
        message.Text = error is LauncherException ? error.Message : "Something went wrong: " + error.Message;
        primary.Text = "Try again";
        secondary.Text = "Close";
        secondary.Enabled = true;
        AcceptButton = primary;
        if (!Visible) Show();
        Activate();
    }

    void OnPrimary()
    {
        if (page == Page.Welcome) choice?.TrySetResult(true);
        else if (page == Page.Error) _ = RunFlowAsync();
    }

    void OnSecondary()
    {
        if (page == Page.Welcome) choice?.TrySetResult(false);
        else if (page == Page.Progress) cancel.Cancel();
        else _ = CloseAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!closing && e.CloseReason == CloseReason.WindowsShutDown)
        {
            closing = true;
            Log.Info("Windows is shutting down");
            Task.Run(Device.StopAsync).Wait(TimeSpan.FromSeconds(25));
        }
        else if (!closing && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            OnSecondary();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await RunFlowAsync();
    }

    async Task RunFlowAsync()
    {
        if (busy) return;
        busy = true;
        cancel = new CancellationTokenSource();
        try
        {
            await RunAsync(cancel.Token);
            busy = false;
            closing = true;
            Close();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            busy = false;
            await CloseAsync();
        }
        catch (Exception error)
        {
            busy = false;
            Log.Error("Mihon couldn't start", error);
            ShowError(error);
        }
    }

    async Task CloseAsync()
    {
        if (androidStarted)
        {
            ShowProgress("Shutting down Android…", null, cancellable: false);
            await Task.Run(Device.StopAsync);
            androidStarted = false;
        }
        closing = true;
        Close();
    }

    async Task RunAsync(CancellationToken ct)
    {
        ShowProgress("Starting…", null);
        while (!instance.TryAcquire())
        {
            ShowProgress("Waiting for the last Mihon session to close…", null);
            await Task.Delay(500, ct);
        }

        var state = new State();
        if (!Installer.IsRunningInstalledCopy || state.Get("license_accepted") == null)
        {
            var update = Installer.InstalledVersion != null && state.Get("license_accepted") != null;
            ShowWelcome(update);
            choice = new TaskCompletionSource<bool>();
            if (!await choice.Task)
            {
                cancel.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            ShowProgress("Installing…", null);
            Installer.Install(desktopShortcut.Checked && !update);
            if (!update) Settings.Write("copy_backups", copyBackups.Checked ? "true" : "false");
            state.Set("license_accepted", DateTime.Now.ToString("yyyy-MM-dd"));
        }

        if (resetAndroid && !await ResetAndroidAsync(state)) return;
        var settings = Settings.Load();
        await Task.Run(() => PrepareAsync(state, settings, ct), ct);
        await Task.Run(() => LaunchAsync(state, settings, ct), ct);
    }

    async Task<bool> ResetAndroidAsync(State state)
    {
        var answer = MessageBox.Show(this,
            "Delete the Android device and start over?\n\nThis removes your Mihon library, downloads and settings inside Android. " +
            "Make a backup in Mihon first if you want to keep them.",
            AppInfo.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return false;
        ShowProgress("Deleting the Android device…", null, cancellable: false);
        await Task.Run(Device.StopAsync);
        await Task.Run(() => Installer.DeleteDirectory(Paths.AvdHome));
        state.Remove("provisioned");
        state.Remove("android_trimmed");
        state.Remove("android_hardware");
        state.Remove("mihon_restarted");
        state.Remove("android_stuck");
        Log.Info("Android device reset");
        return true;
    }

    async Task PrepareAsync(State state, Settings settings, CancellationToken ct)
    {
        if (!AndroidSdk.IsInstalled(state)) await AndroidSdk.InstallAsync(state, Report, ct);
        if (!Scrcpy.IsInstalled(state)) await Scrcpy.InstallAsync(state, Report, ct);
    }

    void StartAndroid(State state, Settings settings, string gpu)
    {
        AndroidSdk.WriteAvd(state.Get("system_image"), settings, gpu);
        // Android's process starts as Mihon.exe, unless that already failed on this PC (see Device.StartAsMihon).
        var asMihon = Device.CanStartAsMihon(settings, gpu) && state.Get("start_as_mihon") != "failed";
        // Forgotten until Android has booted, so an interrupted cold boot is retried next time.
        var hardware = Device.HardwareSignature(settings, gpu, asMihon);
        var changed = state.Get("android_hardware") != hardware;
        state.Remove("android_hardware");
        if (changed) Log.Info("Emulated hardware changed, so Android starts without its saved state once");
        // When Mihon's process is ended rather than closed (the launcher updated it, say), the emulator keeps the
        // pictures it showed, and the quick-boot snapshot would bring them back into memory at every start. Starting
        // afresh clears them. Crashes can leave pictures behind too, so the saved graphics also have a limit.
        var restarted = state.Get("mihon_restarted") != null;
        if (restarted) Log.Info("Mihon was restarted last time, so Android starts without its saved state to clear what that left behind");
        var textures = new FileInfo(Path.Combine(Paths.AvdDir, "snapshots", "default_boot", "textures.bin"));
        var heavy = textures.Exists && textures.Length > SavedGraphicsLimit;
        if (heavy) Log.Info($"Android's saved graphics take {textures.Length >> 20} MB, so it starts without its saved state");
        var stuck = state.Get("android_stuck") != null;
        if (stuck) Log.Info("Android got stuck showing Mihon's splash screen last time, so it starts without its saved state");
        var fresh = coldBoot || changed || restarted || heavy || stuck;
        Device.Start(settings, gpu, fresh, asMihon);
        fromSavedState = !fresh;
        state.Remove("mihon_restarted");
        state.Remove("android_stuck");
        startedGpu = gpu;
    }

    /// <summary>This launch started Android from its saved state (a quick start).</summary>
    bool fromSavedState;

    // Mihon's own pictures settle at about 64 MB here (1374x918 window); far more means something was left behind.
    const long SavedGraphicsLimit = 512L << 20;

    /// <summary>Notes what this session left that the quick-boot snapshot shouldn't bring back.</summary>
    static void RememberFreshStart(State state)
    {
        if (MihonApp.Restarted) state.Set("mihon_restarted", "1");
        if (MihonApp.SplashStuck) state.Set("android_stuck", "1");
    }

    /// <summary>The graphics Android was started with in this launch, if it was started.</summary>
    string startedGpu;

    async Task LaunchAsync(State state, Settings settings, CancellationToken ct)
    {
        var firstStart = !Directory.Exists(Path.Combine(Paths.AvdDir, "snapshots"));
        Report(firstStart ? "Starting Android for the first time. This takes a minute or two…" : "Starting Android…", null);
        androidStarted = true;
        // The emulator's own "auto" picks software drawing when it has no window, which makes Android laggy,
        // so ours means: the graphics card, unless that already failed on this PC.
        var gpu = settings.Gpu == "auto" ? state.Get("gpu_fallback") ?? "host" : settings.Gpu;
        if (!await Device.IsBootedAsync(ct) && !Device.IsRunning()) StartAndroid(state, settings, gpu);

        // Ask GitHub about new Mihon releases while Android boots.
        var latest = settings.AutoUpdate ? CheckForUpdateAsync(settings, ct) : Task.FromResult<MihonRelease>(null);
        try
        {
            await Device.WaitForBootAsync(ct);
        }
        catch (LauncherException e) when (settings.Gpu == "auto" && gpu == "host" && !Device.IsRunning())
        {
            Log.Warn("Android didn't start with the graphics card, trying software drawing: " + e.Message);
            Report("Starting Android…", null);
            StartAndroid(state, settings, "software");
            await Device.WaitForBootAsync(ct);
            // Only now is it clear that the graphics card was the problem (and not, say, the whole emulator).
            state.Set("gpu_fallback", "software");
        }
        if (Device.StartAsMihonFailed) state.Set("start_as_mihon", "failed");
        // The hardware Android's saved state will be for, including how its process was really started.
        if (startedGpu != null) state.Set("android_hardware", Device.HardwareSignature(settings, startedGpu, Device.RunningAsMihon));
        Device.KeepAtFullSpeed();
        if (state.Get("provisioned") == null)
        {
            await Device.ProvisionAsync(ct);
            state.Set("provisioned", "1");
        }
        await Device.TrimAsync(settings.TrimAndroid, state, status => Report(status, null), ct);
        await Device.SetAnimationsAsync(settings.Animations, ct);
        await Device.SetRendererAsync(settings.Renderer, ct);
        await Device.DisableOnScreenKeyboardsAsync(ct);
        await Device.StopLogMirrorAsync(ct);

        var installed = await MihonApp.GetInstalledVersionAsync(ct);
        var release = await latest;
        if (release != null && (installed == null || MihonApp.CompareVersions(installed, release.Version) < 0))
        {
            var status = installed == null ? $"Downloading Mihon {release.Tag}…" : $"Updating Mihon from v{installed} to {release.Tag}…";
            var apk = await MihonApp.DownloadAsync(release, (done, total) => Report(status, total > 0 ? (double)done / total : null), ct);
            Report($"Installing Mihon {release.Tag}…", null);
            await MihonApp.InstallAsync(apk, ct);
            Log.Info($"Mihon {installed ?? "(not installed)"} -> {release.Version}");
        }
        else if (installed == null)
        {
            throw new LauncherException("Mihon isn't installed yet, and GitHub couldn't be reached to download it. Check the internet connection and try again.");
        }

        if (settings.ExtensionInstaller == "private")
        {
            try { await MihonApp.UsePrivateExtensionInstallerAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Warn("Setting Mihon's extension installer failed: " + e.Message); }
        }
        RememberFreshStart(state);

        Report("Opening Mihon…", null);
        var bounds = WindowBounds.Load(state);
        WindowBounds closedAt = null;
        if (settings.View == "emulator")
        {
            try
            {
                closedAt = await RunEmulatorViewAsync(settings, bounds, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Warn("Showing Mihon straight from the emulator didn't work, so scrcpy shows it: " + e);
                // scrcpy makes a display of its own; Mihon's tasks move off this one when it goes. Android's saved state
                // then no longer matches the display the emulator has on record, so the next start begins afresh.
                await Device.RemoveMihonDisplayAsync();
                state.Remove("android_hardware");
            }
        }
        closedAt ??= await RunScrcpyAsync(settings, bounds, ct);
        state.Set("window", closedAt.ToString());
        RememberFreshStart(state);

        if (settings.CopyBackups)
        {
            try { await MihonApp.CopyBackupsAsync(CancellationToken.None); }
            catch (Exception e) { Log.Warn("Copying backups failed: " + e.Message); }
        }
        if (!settings.KeepRunning)
        {
            await Device.StopAsync();
            androidStarted = false;
        }
    }

    /// <summary>
    /// Shows Mihon in an <see cref="EmulatorView"/>, drawn from the emulator's own picture of Mihon's display,
    /// until the window is closed. Returns where the window was.
    /// </summary>
    async Task<WindowBounds> RunEmulatorViewAsync(Settings settings, WindowBounds bounds, CancellationToken ct)
    {
        var (port, token) = await Device.GrpcEndpointAsync(ct);
        using var grpc = new GrpcConnection(port, token);
        var dpi = Scrcpy.AndroidDpi(settings);
        await Device.EnableMultiDisplayAsync(ct);
        if (!await Device.SetMihonDisplayAsync(bounds.Width, bounds.Height, dpi))
            throw new LauncherException("The emulator couldn't create Mihon's display.");
        var display = await Device.MihonDisplayIdAsync(ct);
        if (settings.Renderer == "vulkan" && fromSavedState) await Device.RenewMihonDisplayAsync(display, bounds.Width, bounds.Height, dpi, ct);
        await MihonApp.ShowOnDisplayAsync(display, ct);

        // Every time Mihon is shown, its splash screen is checked once it should have gone, until the window closes.
        using var splashWatch = new CancellationTokenSource();
        async Task WatchSplashAsync()
        {
            try { await MihonApp.ClearStuckSplashAsync(splashWatch.Token); }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log.Warn("Couldn't check Mihon's splash screen: " + e.Message); }
        }

        // Back on Mihon's first screen closes Mihon, and with no home screen on its display the window would stay
        // black. So once Back presses settle, Mihon is opened again if it's gone.
        var backPresses = 0;
        async Task KeepMihonShownAsync()
        {
            var press = Interlocked.Increment(ref backPresses);
            await Task.Delay(800);
            if (press != Volatile.Read(ref backPresses)) return;
            try
            {
                if (await MihonApp.IsOnDisplayAsync(display, CancellationToken.None)) return;
                Log.Info("Back closed Mihon; opening it again");
                await MihonApp.ShowOnDisplayAsync(display, CancellationToken.None);
                _ = WatchSplashAsync();
            }
            catch (Exception e)
            {
                Log.Warn("Couldn't check that Mihon is still showing: " + e.Message);
            }
        }

        var closed = new TaskCompletionSource<WindowBounds>(TaskCreationOptions.RunContinuationsAsynchronously);
        EmulatorView view = null;
        Invoke((Action)(() =>
        {
            view = new EmulatorView(grpc, bounds, dpi);
            view.SizeWanted += size => _ = Device.SetMihonDisplayAsync(size.Width, size.Height, dpi);
            view.FilesDropped += files => _ = MihonApp.ReceiveFilesAsync(files);
            view.BackPressed += () => _ = KeepMihonShownAsync();
            view.FormClosed += (_, _) => closed.TrySetResult(view.NormalBounds);
            view.Show();
            Taskbar.AdoptWindow(view.Handle);
        }));
        var started = await Task.WhenAny(view.FirstFrame, closed.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));
        if (started != view.FirstFrame && started != closed.Task)
        {
            Invoke((Action)view.Close);
            ct.ThrowIfCancellationRequested();
            throw new LauncherException("No picture came from the emulator.");
        }
        Ui(Hide);
        Log.Info($"Showing Mihon on display {display}, straight from the emulator");
        _ = WatchSplashAsync();

        var result = await closed.Task;
        splashWatch.Cancel();
        Log.Info("Mihon window closed");
        if (!Device.IsRunning()) throw new LauncherException("Android stopped unexpectedly.", Log.Tail(Log.EmulatorLog));
        return result;
    }

    /// <summary>Shows Mihon in a scrcpy window (a video stream of Mihon's display) until it's closed.</summary>
    async Task<WindowBounds> RunScrcpyAsync(Settings settings, WindowBounds bounds, CancellationToken ct)
    {
        using var session = Scrcpy.Start(settings, bounds);
        var scrcpy = session.Process;
        var window = await Scrcpy.WaitForWindowAsync(scrcpy, ct);
        if (window == IntPtr.Zero)
        {
            Proc.TryKill(scrcpy);
            throw new LauncherException("The Mihon window didn't open.", Log.Tail(Log.ScrcpyLog));
        }
        Ui(() =>
        {
            Taskbar.AdoptWindow(window);
            Hide();
            // From here on this process only waits for the window to close, so hand its memory back to Windows.
            Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        });

        await EnsureMihonShownAsync(session, ct);

        bounds = await Scrcpy.TrackUntilClosedAsync(scrcpy, window, bounds);
        Log.Info($"Mihon window closed (scrcpy exit code {scrcpy.ExitCode})");
        if (scrcpy.ExitCode == 2 && !Device.IsRunning())
            throw new LauncherException("Android stopped unexpectedly.", Log.Tail(Log.EmulatorLog));
        return bounds;
    }

    /// <summary>
    /// Right after Android boots, scrcpy can ask to start Mihon before its display exists ("No known
    /// display id") and leave the window empty, so check that Mihon is there and start it if not.
    /// </summary>
    static async Task EnsureMihonShownAsync(ScrcpySession session, CancellationToken ct)
    {
        try
        {
            if (await Task.WhenAny(session.DisplayId, Task.Delay(TimeSpan.FromSeconds(20), ct)) != session.DisplayId)
            {
                Log.Warn("scrcpy didn't report a display for Mihon");
                return;
            }
            var display = await session.DisplayId;
            for (var attempt = 1; attempt <= 3 && !session.Process.HasExited; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                if (await MihonApp.IsOnDisplayAsync(display, ct)) return;
                Log.Warn($"Mihon isn't on display {display} (attempt {attempt}); starting it there");
                await MihonApp.StartOnDisplayAsync(display, ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn("Couldn't check that Mihon is showing: " + e.Message);
        }
    }

    static async Task<MihonRelease> CheckForUpdateAsync(Settings settings, CancellationToken ct)
    {
        try
        {
            return await MihonApp.GetLatestReleaseAsync(settings, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn("Couldn't check for a Mihon update: " + e.Message);
            return null;
        }
    }

    void Report(string status, double? fraction) => Ui(() => ShowProgress(status, fraction));

    void Ui(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    static void OpenExternal(string target)
    {
        try { Process.Start(target)?.Dispose(); }
        catch (Exception e) { Log.Warn($"Couldn't open {target}: {e.Message}"); }
    }
}
