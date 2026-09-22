using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BluetoothSafetyLock
{
    /// <summary>
    /// Built-in auto-updater for GitHub Releases.
    /// Flow mirrors a classic desktop updater (Doggy Player / electron-updater):
    /// check at launch, notify the user, download in the background, then apply
    /// on next start — or immediately via "Install update &amp; restart".
    /// This is the only network traffic the app ever performs, and it can be
    /// switched off entirely in Settings. No telemetry, no accounts: the request
    /// carries nothing but a standard GitHub "latest release" lookup.
    /// </summary>
    public static class UpdaterService
    {
        private const string Repo = "nRn-World/BluetoothSafetyLock";
        private const string ReleasesApiUrl = $"https://api.github.com/repos/{Repo}/releases/latest";

        /// <summary>Name of the staged update placed next to the running exe.</summary>
        private const string StagedFileName = "BluetoothSafetyLock.update.exe";
        /// <summary>The running exe is parked under this name during a swap; deleted on a later start.</summary>
        private const string BackupFileName = "BluetoothSafetyLock.old.exe";

        private static readonly HttpClient _http = CreateClient();
        private static int _downloadInProgress;

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new SocketsHttpHandler
            {
                // Fresh DNS lookups; a long-lived tray process should not pin stale connections.
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });
            client.Timeout = TimeSpan.FromSeconds(30);
            // GitHub API requires a User-Agent header.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BluetoothSafetyLock-Updater");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        /// <summary>Full path of the running executable. Declared BEFORE CurrentVersion: static initializers run in declaration order.</summary>
        public static string ExePath { get; } =
            Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath ?? string.Empty;

        /// <summary>Version of the currently running executable (0.0.0 when unknown).</summary>
        public static Version CurrentVersion { get; } = ReadFileVersion(ExePath);

        /// <summary>Version of the staged update waiting to be applied (null when none).</summary>
        public static Version? PendingVersion
        {
            get
            {
                try
                {
                    return File.Exists(StagedPath) ? ReadFileVersion(StagedPath) : null;
                }
                catch { return null; }
            }
        }

        /// <summary>An update has been downloaded and will be applied at next start.</summary>
        public static bool HasStagedUpdate => File.Exists(StagedPath);

        private static string StagedPath =>
            Path.Combine(Path.GetDirectoryName(ExePath) ?? ".", StagedFileName);
        private static string BackupPath =>
            Path.Combine(Path.GetDirectoryName(ExePath) ?? ".", BackupFileName);

        private static Version ReadFileVersion(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (Version.TryParse(info.FileVersion, out var v)) return v;
            }
            catch { /* fall through */ }
            return new Version(0, 0, 0);
        }

        /// <summary>
        /// Called once at the very start of Main, before the single-instance mutex:
        /// cleans up a previous swap and applies any staged update. When an update
        /// was applied, a fresh process (the NEW version) is launched and true is
        /// returned — the caller must exit immediately.
        /// Never throws: a failed update must not prevent the app from starting protected.
        /// </summary>
        public static bool ApplyPendingInstall()
        {
            try
            {
                TryDeleteBackup();

                if (!File.Exists(StagedPath)) return false;

                Version? pending = PendingVersion;
                SwapInPlace(StagedPath, BackupPath);
                Logger.Info($"Updater: staged update {pending} applied; launching new version.");
                StartNewInstance();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Updater: applying pending update failed; continuing with existing binary.", ex);
                return false;
            }
        }

        /// <summary>
        /// Swaps the running exe with the staged update right now and starts the new
        /// version. The caller should shut the current instance down immediately after.
        /// </summary>
        public static void RestartToInstall()
        {
            try
            {
                if (File.Exists(StagedPath))
                {
                    SwapInPlace(StagedPath, BackupPath);
                    Logger.Info("Updater: update swapped in; restarting into new version.");
                }
            }
            catch (Exception ex)
            {
                // The running binary is intact (SwapInPlace restores it on failure);
                // a plain restart is the safe fallback.
                Logger.Error("Updater: in-place swap failed; restarting without update.", ex);
            }
            StartNewInstance();
        }

        /// <summary>
        /// Queries GitHub for the newest published release.
        /// Returns (version, zip download url, release page) or null when
        /// up-to-date, no release exists, or the check could not complete.
        /// </summary>
        public static async Task<(Version Version, string AssetUrl, string ReleaseUrl)?> CheckForUpdateAsync(
            CancellationToken ct = default)
        {
            try
            {
                using var response = await _http.GetAsync(ReleasesApiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode == 404)
                {
                    Logger.Info("Updater: repository has no releases yet.");
                    return null;
                }
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = doc.RootElement;

                if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
                string tag = tagEl.GetString() ?? "";

                if (!TryParseVersionTag(tag, out var latest))
                {
                    Logger.Warn($"Updater: could not parse release tag '{tag}'.");
                    return null;
                }

                // Only newer builds count; equal or lower never triggers (no downgrade loops).
                if (latest <= CurrentVersion) return null;

                string? assetUrl = null;
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (!asset.TryGetProperty("name", out var nameEl)) continue;
                        string name = nameEl.GetString() ?? "";
                        // The official zip is BluetoothSafetyLock-vX.Y.Z.zip; accept any zip for robustness.
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                            name.StartsWith("BluetoothSafetyLock", StringComparison.OrdinalIgnoreCase))
                        {
                            if (asset.TryGetProperty("browser_download_url", out var urlEl))
                            {
                                assetUrl = urlEl.GetString();
                                break;
                            }
                        }
                    }
                }
                if (string.IsNullOrEmpty(assetUrl))
                {
                    Logger.Warn("Updater: latest release has no matching zip asset; skipping.");
                    return null;
                }

                string htmlUrl = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() ?? "" : "";
                return (latest, assetUrl!, htmlUrl);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Offline or rate-limited is a normal, silent state for a tray app.
                Logger.Warn($"Updater: check failed. {ex.Message}");
                return null;
            }
        }

        /// <summary>Downloads the release zip, extracts the exe and stages it next to the running one.</summary>
        public static async Task<string> DownloadAndStageAsync(
            string assetUrl,
            Action<long, long>? progressBytes = null,
            CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _downloadInProgress, 1) == 1)
                throw new InvalidOperationException("An update download is already in progress.");

            string tempZip = Path.Combine(Path.GetTempPath(), $"BluetoothSafetyLock-{Guid.NewGuid():N}.zip");
            string tempDir = Path.Combine(Path.GetTempPath(), $"BluetoothSafetyLock-{Guid.NewGuid():N}");
            long written = 0;

            try
            {
                Directory.CreateDirectory(tempDir);

                using (var response = await _http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    await using var http = await response.Content.ReadAsStreamAsync(ct);
                    await using var file = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                    var buffer = new byte[81920];
                    int read;
                    while ((read = await http.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                        written += read;
                        progressBytes?.Invoke(written, -1);
                    }
                }

                // Extract only what is needed: the exe (self-contained single-file build).
                string exeInZip = FindExeInZip(tempZip, tempDir);

                string stagePath = StagedPath;
                File.Copy(exeInZip, stagePath, overwrite: true);

                Logger.Info($"Updater: staged update ready ({written:N0} bytes downloaded).");
                return stagePath;
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* temp cleanup is best-effort */ }
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
                Interlocked.Exchange(ref _downloadInProgress, 0);
            }
        }

        /// <summary>Extracts the zip and returns the full path of the contained BluetoothSafetyLock exe.</summary>
        private static string FindExeInZip(string zipPath, string extractDir)
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir);

            var candidates = Directory.EnumerateFiles(extractDir, "*.exe", SearchOption.AllDirectories)
                .Where(p => Path.GetFileName(p).StartsWith("BluetoothSafetyLock", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
                candidates = Directory.EnumerateFiles(extractDir, "*.exe", SearchOption.AllDirectories).ToList();

            if (candidates.Count == 0)
                throw new InvalidOperationException("Downloaded release zip contains no executable.");

            // Largest exe wins: the self-contained binary is by far the biggest file.
            return candidates.OrderByDescending(f => new FileInfo(f).Length).First();
        }

        /// <summary>
        /// old exe → .old, staged → exe. Renaming the running exe is allowed on NTFS
        /// (only deletion of a running image is not), so this works even while the
        /// current instance keeps executing its mapped image. If the second move
        /// fails, the old binary is restored so the app always stays launchable.
        /// </summary>
        private static void SwapInPlace(string newVersionPath, string oldPath)
        {
            TryDeleteBackup();

            File.Move(ExePath, oldPath);
            try
            {
                File.Move(newVersionPath, ExePath);
            }
            catch
            {
                try { File.Move(oldPath, ExePath); } catch { /* nothing more we can do */ }
                throw;
            }
        }

        private static void TryDeleteBackup()
        {
            try
            {
                if (File.Exists(BackupPath)) File.Delete(BackupPath);
            }
            catch (Exception ex)
            {
                // The backup may still be mapped by a running old instance; left for a later start.
                Logger.Info($"Updater: backup cleanup postponed. {ex.Message}");
            }
        }

        private static void StartNewInstance()
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = ExePath, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error("Updater: could not launch the updated instance.", ex);
            }
        }

        /// <summary>Accepts "v1.2.3", "1.2.3" and "1.2.3-beta.1" style tags.</summary>
        private static bool TryParseVersionTag(string tag, out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(tag)) return false;

            string s = tag.TrimStart('v', 'V');
            int cut = s.IndexOf('-');
            if (cut >= 0) s = s[..cut];

            return Version.TryParse(s, out version!);
        }
    }
}
