using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MihonWindows;

sealed class ProcResult
{
    public int ExitCode;
    public string Output = "";
    public bool TimedOut;

    public bool Ok => !TimedOut && ExitCode == 0;
}

static class Proc
{
    /// <summary>Keeps our Android tools apart from any Android SDK or adb already on this PC.</summary>
    static readonly Dictionary<string, string> AndroidEnvironment = new()
    {
        ["ANDROID_HOME"] = Paths.Sdk,
        ["ANDROID_SDK_ROOT"] = Paths.Sdk,
        ["ANDROID_AVD_HOME"] = Paths.AvdHome,
        ["ANDROID_USER_HOME"] = Paths.AndroidUserHome,
        ["ANDROID_EMULATOR_HOME"] = Paths.AndroidUserHome,
        ["ANDROID_ADB_SERVER_PORT"] = Device.AdbServerPort.ToString(),
        ["ANDROID_SERIAL"] = Device.Serial,
        ["ADB"] = Paths.Adb,
        ["SCRCPY_ICON_DIR"] = Paths.IconDir,
    };

    public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(Quote)))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe),
        };
        foreach (var variable in AndroidEnvironment) psi.EnvironmentVariables[variable.Key] = variable.Value;
        return psi;
    }

    /// <summary>Runs a command and collects its output. Kills it if it runs longer than <paramref name="timeout"/>.</summary>
    public static async Task<ProcResult> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = StartInfo(exe, args);
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = Encoding.UTF8;

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.Exited += (_, _) => exited.TrySetResult(true);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (ct.Register(() => exited.TrySetCanceled()))
        {
            var finished = await Task.WhenAny(exited.Task, Task.Delay(timeout)) == exited.Task;
            if (!finished || exited.Task.IsCanceled)
            {
                TryKill(process);
                ct.ThrowIfCancellationRequested();
                lock (output) return new ProcResult { ExitCode = -1, TimedOut = true, Output = output.ToString() };
            }
        }
        process.WaitForExit(); // flushes the output readers
        lock (output) return new ProcResult { ExitCode = process.ExitCode, Output = output.ToString() };
    }

    public static Task WaitForExitAsync(Process process)
    {
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exited.TrySetResult(true);
        if (process.HasExited) exited.TrySetResult(true);
        return exited.Task;
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch
        {
            // Already gone.
        }
    }

    /// <summary>Quotes one argument the way Windows programs parse their command line.</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var quoted = new StringBuilder("\"");
        for (var i = 0; i < arg.Length; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                backslashes++;
                i++;
            }
            if (i == arg.Length)
            {
                quoted.Append('\\', backslashes * 2);
                break;
            }
            if (arg[i] == '"') quoted.Append('\\', backslashes * 2 + 1).Append('"');
            else quoted.Append('\\', backslashes).Append(arg[i]);
        }
        return quoted.Append('"').ToString();
    }
}
