using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MihonWindows;

static class Net
{
    static readonly HttpClient Http = CreateClient(DecompressionMethods.GZip | DecompressionMethods.Deflate);

    // Files come uncompressed: Google gzips its zips on the fly when asked, and a resumed (ranged)
    // request then returns the middle of that gzip stream, which can't be decoded.
    static readonly HttpClient FileHttp = CreateClient(DecompressionMethods.None);

    static HttpClient CreateClient(DecompressionMethods decompression)
    {
        var handler = new HttpClientHandler { AutomaticDecompression = decompression };
        // Requests set their own timeouts; large downloads can take a long time.
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MihonForWindows/" + AppInfo.Version);
        return client;
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct, int timeoutSeconds = 30)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (url.StartsWith("https://api.github.com/", StringComparison.Ordinal))
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
        try
        {
            using var response = await Http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out connecting to " + new Uri(url).Host);
        }
    }

    public static async Task<object> GetJsonAsync(string url, CancellationToken ct) =>
        new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(await GetStringAsync(url, ct));

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destination"/>, resuming a partial download when possible.
    /// The file is only kept when its size and hash ("sha1:…" or "sha256:…") match.
    /// </summary>
    public static async Task DownloadAsync(string url, string destination, long size, string hash, Action<long, long> progress, CancellationToken ct)
    {
        if (File.Exists(destination) && (size <= 0 || new FileInfo(destination).Length == size) && HashMatches(destination, hash))
        {
            progress?.Invoke(size, size);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        var part = destination + ".part";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadPartAsync(url, part, size, progress, ct);
                break;
            }
            catch (Exception e) when (attempt < 5 && !ct.IsCancellationRequested && e is HttpRequestException or IOException or TimeoutException)
            {
                Log.Warn($"Download of {url} failed (attempt {attempt}): {e.Message}");
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct);
            }
        }

        if ((size > 0 && new FileInfo(part).Length != size) || !HashMatches(part, hash))
        {
            File.Delete(part);
            throw new LauncherException($"The download of {Path.GetFileName(destination)} was damaged. Please try again.");
        }
        if (File.Exists(destination)) File.Delete(destination);
        File.Move(part, destination);
    }

    static async Task DownloadPartAsync(string url, string part, long size, Action<long, long> progress, CancellationToken ct)
    {
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (size > 0 && existing == size) return;
        if (size > 0 && existing > size) existing = 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await FileHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent) existing = 0;
        response.EnsureSuccessStatusCode();

        var total = size > 0 ? size : existing + (response.Content.Headers.ContentLength ?? 0);
        using var input = await response.Content.ReadAsStreamAsync();
        using var output = new FileStream(part, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

        // A read that makes no progress for a minute counts as a dropped connection.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var abort = stall.Token.Register(() => response.Dispose());
        var buffer = new byte[1 << 20];
        var done = existing;
        var sinceReport = Stopwatch.StartNew();
        while (true)
        {
            stall.CancelAfter(TimeSpan.FromSeconds(60));
            int read;
            try
            {
                read = await input.ReadAsync(buffer, 0, buffer.Length, stall.Token);
            }
            catch (Exception) when (stall.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("The download stopped making progress.");
            }
            if (read == 0) break;
            await output.WriteAsync(buffer, 0, read, ct);
            done += read;
            if (sinceReport.ElapsedMilliseconds > 250)
            {
                progress?.Invoke(done, total);
                sinceReport.Restart();
            }
        }
        progress?.Invoke(done, total);
    }

    static bool HashMatches(string file, string hash)
    {
        if (string.IsNullOrEmpty(hash)) return true;
        var colon = hash.IndexOf(':');
        var algorithm = hash.Substring(0, colon).ToLowerInvariant();
        var expected = hash.Substring(colon + 1).Trim().ToLowerInvariant();
        using HashAlgorithm hasher = algorithm switch
        {
            "sha1" => SHA1.Create(),
            "sha256" => SHA256.Create(),
            _ => throw new ArgumentException("Unknown hash algorithm " + algorithm),
        };
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return BitConverter.ToString(hasher.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() == expected;
    }

    /// <summary>
    /// Extracts a zip into <paramref name="destination"/>, replacing it. When every entry sits in one
    /// top-level folder (as in Google's and scrcpy's zips), that folder's contents land directly in the destination.
    /// </summary>
    public static void ExtractZip(string zip, string destination, Action<double> progress, CancellationToken ct)
    {
        var staging = destination + ".partial";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

        string prefix;
        long total;
        using (var archive = ZipFile.OpenRead(zip))
        {
            var tops = archive.Entries.Select(e => e.FullName.Split('/')[0]).Distinct().ToList();
            prefix = tops.Count == 1 && archive.Entries.All(e => e.FullName.IndexOf('/') > 0) ? tops[0] + "/" : "";
            total = Math.Max(1, archive.Entries.Sum(e => e.Length));
            foreach (var entry in archive.Entries)
            {
                var relative = entry.FullName.Substring(prefix.Length);
                var target = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (relative.Length > 0 && !target.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{Path.GetFileName(zip)} contains an unsafe path: {entry.FullName}");
            }
        }

        // Windows' own tar (bsdtar) reads Zip64 entries over 4 GB, such as the 5.5 GB system.img in
        // Google's system images, which .NET Framework's ZipArchive can't open.
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (File.Exists(tar)) ExtractWithTar(tar, zip, staging, prefix.Length > 0, total, progress, ct);
        else ExtractWithZipArchive(zip, staging, prefix, total, progress, ct);

        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        Directory.Move(staging, destination);
        progress?.Invoke(1);
    }

    static void ExtractWithTar(string tar, string zip, string staging, bool stripTopFolder, long total, Action<double> progress, CancellationToken ct)
    {
        var args = new List<string> { "-x", "-f", zip, "-C", staging };
        if (stripTopFolder) args.AddRange(new[] { "--strip-components", "1" });
        var psi = new ProcessStartInfo(tar, string.Join(" ", args.Select(Proc.Quote)))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi);
        var errors = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        while (!process.WaitForExit(500))
        {
            if (ct.IsCancellationRequested)
            {
                Proc.TryKill(process);
                ct.ThrowIfCancellationRequested();
            }
            progress?.Invoke(Math.Min(0.99, DirectorySize(staging) / (double)total));
        }
        if (process.ExitCode != 0)
            throw new LauncherException($"Couldn't unpack {Path.GetFileName(zip)}.", errors.Result.Trim());
    }

    static long DirectorySize(string path)
    {
        long size = 0;
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                file.Refresh(); // directory listings report stale sizes for files still being written
                size += file.Length;
            }
            catch (IOException)
            {
            }
        }
        return size;
    }

    static void ExtractWithZipArchive(string zip, string staging, string prefix, long total, Action<double> progress, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zip);
        long done = 0;
        var buffer = new byte[1 << 20];
        var sinceReport = Stopwatch.StartNew();
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var relative = entry.FullName.Substring(prefix.Length);
            if (relative.Length == 0) continue;
            var target = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
            if (relative.EndsWith("/"))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using (var input = entry.Open())
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    done += read;
                    if (sinceReport.ElapsedMilliseconds > 250)
                    {
                        progress?.Invoke((double)done / total);
                        sinceReport.Restart();
                    }
                }
            }
            File.SetLastWriteTime(target, entry.LastWriteTime.DateTime);
        }
    }
}
