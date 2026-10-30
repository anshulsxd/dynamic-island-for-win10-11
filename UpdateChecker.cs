using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IslandEye
{
    public record ReleaseInfo(Version Version, string Tag, string Title, string Notes, string AssetUrl, string? Sha256);

    /// <summary>Checks the newest GitHub Release of AppInfo.GitHubRepo and installs it.</summary>
    public static class UpdateChecker
    {
        private static readonly HttpClient Http = Create();

        private static HttpClient Create()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Island-Updater/1.0");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        /// <summary>Returns the latest release, or null if there is none. Throws if GitHub can't be reached.</summary>
        public static async Task<ReleaseInfo?> GetLatestAsync()
        {
            if (!AppInfo.RepoConfigured) return null;

            string url = $"https://api.github.com/repos/{AppInfo.GitHubRepo}/releases/latest";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var resp = await Http.GetAsync(url, cts.Token);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // no release yet
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed)) return null;
            var ver = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));

            string title = root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
            string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            string? assetUrl = null, sha = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.GetProperty("name").GetString() ?? "";
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    bool exact = name.Equals("Island.exe", StringComparison.OrdinalIgnoreCase);
                    if (assetUrl != null && !exact) continue;

                    assetUrl = a.GetProperty("browser_download_url").GetString();
                    sha = null;
                    if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
                    {
                        string? s = d.GetString();
                        if (s != null && s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = s[7..];
                    }
                    if (exact) break;
                }
            }
            if (assetUrl == null) return null;
            return new ReleaseInfo(ver, tag, title, notes, assetUrl, sha);
        }

        private static async Task DownloadAsync(string url, string dest, IProgress<double>? progress)
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;

            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf.AsMemory())) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                read += n;
                if (total > 0) progress?.Report((double)read / total.Value);
            }
        }

        /// <summary>
        /// Downloads the new .exe, swaps it in (a running .exe can be renamed but not overwritten)
        /// and starts it. The caller should shut the current instance down afterwards.
        /// </summary>
        public static async Task InstallAsync(ReleaseInfo rel, IProgress<double> progress)
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't locate the running program.");
            string newPath = exe + ".new", oldPath = exe + ".old";

            if (!Uri.TryCreate(rel.AssetUrl, UriKind.Absolute, out var u) ||
                u.Scheme != Uri.UriSchemeHttps ||
                !u.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The update isn't hosted on GitHub, so it was blocked.");

            await DownloadAsync(rel.AssetUrl, newPath, progress);

            // integrity checks
            using (var fs = File.OpenRead(newPath))
            {
                int b0 = fs.ReadByte(), b1 = fs.ReadByte();
                if (b0 != 'M' || b1 != 'Z') { fs.Close(); File.Delete(newPath); throw new InvalidOperationException("The downloaded file isn't a Windows program."); }
            }
            if (!string.IsNullOrEmpty(rel.Sha256))
            {
                string hash;
                using (var fs = File.OpenRead(newPath)) hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
                if (!hash.Equals(rel.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(newPath);
                    throw new InvalidOperationException("The downloaded file failed its integrity check.");
                }
            }

            try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
            File.Move(exe, oldPath);
            try { File.Move(newPath, exe); }
            catch { File.Move(oldPath, exe); throw; }

            Process.Start(new ProcessStartInfo(exe, $"--wait-pid {Environment.ProcessId}")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!
            });
        }
    }
}
