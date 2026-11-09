#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Multiplayer.Common;
using RestSharp;
using Verse;

namespace Multiplayer.Client.Util;

/// <summary>
/// Self-update for installs made from this fork's CI builds.
///
/// CI publishes one GitHub release per commit, tagged <c>build-&lt;hash&gt;</c>, containing <c>Multiplayer.zip</c> and
/// <c>version.txt</c> (the hash). The newest one is marked "latest". The zip contains an <c>AutoUpdate.txt</c> marker
/// holding the GitHub repository ("owner/name"); installs without it (Steam, local dev symlinks) never self-update.
///
/// Two flows:
/// - At startup: update to the latest build, then offer a restart. This keeps hosts on the newest version.
/// - When a host rejects us for running a different build: update to the host's exact build, restart, reconnect.
///
/// Only the commit hash ever comes from a remote server; the download URL is always built from the local marker, so a
/// server cannot make the client install arbitrary files.
/// </summary>
public static class AutoUpdater
{
    private const string MarkerFile = "AutoUpdate.txt";
    private const string AsideSuffix = ".mp-old";
    private static readonly string[] ManagedDirs = ["About", "Assemblies", "AssembliesCustom", "Defs", "Languages", "Textures"];
    private static readonly Regex HashRegex = new("^[0-9a-fA-F]{7,40}$");
    private static readonly Regex RepoRegex = new(@"^[\w.-]+/[\w.-]+$");

    private static string RootDir => Multiplayer.modContentPack.RootDir;

    /// The hash of the build that was installed on disk this session but isn't running yet.
    public static string? InstalledPendingRestart { get; private set; }

    private static bool busy;

    public static string? Repository
    {
        get
        {
            try
            {
                // Never touch a git checkout, even if someone copied the marker into it.
                if (Directory.Exists(Path.Combine(RootDir, ".git")) || File.Exists(Path.Combine(RootDir, ".git")))
                    return null;
                var path = Path.Combine(RootDir, MarkerFile);
                if (!File.Exists(path)) return null;
                var repo = File.ReadAllText(path).Trim();
                return RepoRegex.IsMatch(repo) ? repo : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public static bool CanUpdate => Repository != null && MpVersion.IsReleaseHash(MpVersion.GitHash);

    public static bool IsRunning(string hash) =>
        string.Equals(hash, MpVersion.GitHash, StringComparison.OrdinalIgnoreCase);

    public static void Init()
    {
        CleanupAsideFiles();

        if (!CanUpdate) return;

        // Wait until the game finished loading: updating Defs/Textures while they're being read would be bad.
        LongEventHandler.ExecuteWhenFinished(() => Task.Run(async () =>
        {
            try
            {
                var latest = await FetchLatestHash();
                if (latest == null || IsRunning(latest)) return;

                Log.Message($"[Multiplayer] Updating from {MpVersion.GitHash} to latest build {latest}");
                await DownloadAndInstall(latest);
                OnMainThread.Enqueue(() => Find.WindowStack.Add(new Dialog_MessageBox(
                    $"Multiplayer was updated to the latest build ({latest}).\n\nRestart now to use it? Hosting or joining before restarting will use the old version.",
                    "Restart now",
                    GenCommandLine.Restart,
                    "Later",
                    null,
                    "Multiplayer updated")));
            }
            catch (Exception e)
            {
                Log.Warning($"[Multiplayer] Auto-update check failed: {e}");
            }
        }));
    }

    /// Called from the main menu's Multiplayer button. Reminds about a pending restart.
    public static void RemindIfPendingRestart()
    {
        if (InstalledPendingRestart == null) return;
        Find.WindowStack.Add(new Dialog_MessageBox(
            $"A new Multiplayer build ({InstalledPendingRestart}) is installed but not running yet.",
            "Restart now",
            GenCommandLine.Restart,
            "Later",
            null));
    }

    /// Download the given build, restart the game and rejoin the server. Shows progress and errors in the UI.
    public static void UpdateToAndReconnect(string hash, IConnector connector)
    {
        if (busy) return;
        busy = true;

        LongEventHandler.QueueLongEvent(() =>
        {
            try
            {
                if (InstalledPendingRestart == null || !string.Equals(InstalledPendingRestart, hash, StringComparison.OrdinalIgnoreCase))
                    DownloadAndInstall(hash).GetAwaiter().GetResult();

                OnMainThread.Enqueue(() =>
                {
                    AutoJoinHandler.SetForChildProcess(connector);
                    GenCommandLine.Restart();
                });
            }
            catch (Exception e)
            {
                busy = false;
                Log.Error($"[Multiplayer] Updating to build {hash} failed: {e}");
                var msg = e is BuildNotFoundException
                    ? $"Build {hash} is not available for download (too old, or built locally by the host). The host should restart their game to update to the latest build."
                    : $"Updating to build {hash} failed:\n{e.Message}\n\nSee the log for details.";
                OnMainThread.Enqueue(() => Find.WindowStack.Add(new Dialog_MessageBox(msg)));
            }
        }, $"Downloading Multiplayer build {hash}...", true, null);
    }

    private class BuildNotFoundException(string msg) : Exception(msg);

    private static IRestClient GitHubClient() =>
        new RestClient("https://github.com/") { FollowRedirects = true, Timeout = 120_000 }
            .AddDefaultHeader("User-Agent", "RimWorld-Multiplayer-AutoUpdater");

    private static async Task<string?> FetchLatestHash()
    {
        var repo = Repository!;
        var resp = await GitHubClient().ExecuteAsync(new RestRequest($"{repo}/releases/latest/download/version.txt"));
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            Log.Warning($"[Multiplayer] Couldn't fetch latest version for {repo}: {resp.StatusCode} {resp.ErrorMessage}");
            return null;
        }

        var hash = resp.Content.Trim();
        return HashRegex.IsMatch(hash) ? hash : null;
    }

    private static async Task DownloadAndInstall(string hash)
    {
        if (!HashRegex.IsMatch(hash)) throw new ArgumentException($"Invalid build hash '{hash}'");
        var repo = Repository ?? throw new InvalidOperationException("This install doesn't support auto-update");

        var resp = await GitHubClient().ExecuteAsync(new RestRequest($"{repo}/releases/download/build-{hash}/Multiplayer.zip"));
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new BuildNotFoundException($"Release build-{hash} not found in {repo}");
        if (resp.StatusCode != HttpStatusCode.OK || resp.RawBytes == null || resp.RawBytes.Length == 0)
            throw new Exception($"Download failed: {(int)resp.StatusCode} {resp.StatusCode} {resp.ErrorMessage}");

        Install(resp.RawBytes, repo);
        InstalledPendingRestart = hash;
        Log.Message($"[Multiplayer] Installed build {hash}, restart required");
    }

    /// Replaces the mod's files with the zip's "Multiplayer/..." content. Files that are in use (Windows locks loaded
    /// DLLs) are renamed aside and deleted on the next start.
    private static void Install(byte[] zipBytes, string expectedRepo)
    {
        var root = Path.GetFullPath(RootDir);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.Name.Length == 0) continue; // directory
                var parts = entry.FullName.Replace('\\', '/').Split('/');
                if (parts.Length < 2 || parts[0] != "Multiplayer") continue;

                var rel = string.Join("/", parts.Skip(1));
                var dest = Path.GetFullPath(Path.Combine(root, rel));
                if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Bad path in update zip: {entry.FullName}");

                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                files[dest] = ms.ToArray();
            }
        }

        // Sanity check before touching anything.
        var markerPath = Path.Combine(root, MarkerFile);
        if (!files.TryGetValue(markerPath, out var marker) || System.Text.Encoding.UTF8.GetString(marker).Trim() != expectedRepo)
            throw new Exception("Update zip doesn't look like a Multiplayer build from this repository");
        if (!files.Keys.Any(f => f.EndsWith("Multiplayer.dll", StringComparison.OrdinalIgnoreCase)))
            throw new Exception("Update zip doesn't contain Multiplayer.dll");

        foreach (var kv in files)
        {
            var dest = kv.Key;
            var content = kv.Value;
            if (File.Exists(dest) && File.ReadAllBytes(dest).SequenceEqual(content)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            WriteReplacing(dest, content);
        }

        // Remove files the new build no longer has (e.g. a removed Def or DLL would otherwise still load).
        foreach (var dir in ManagedDirs)
        {
            var full = Path.Combine(root, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var file in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(AsideSuffix) || files.ContainsKey(Path.GetFullPath(file))) continue;
                try { File.Delete(file); }
                catch (Exception) { MoveAside(file); }
            }
        }
    }

    private static void WriteReplacing(string dest, byte[] content)
    {
        try
        {
            File.WriteAllBytes(dest, content);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Locked (loaded DLL on Windows): renaming a file in use is allowed, overwriting isn't.
            MoveAside(dest);
            File.WriteAllBytes(dest, content);
        }
    }

    private static void MoveAside(string file)
    {
        var aside = file + "." + DateTime.UtcNow.Ticks + AsideSuffix;
        File.Move(file, aside);
    }

    private static void CleanupAsideFiles()
    {
        try
        {
            foreach (var dir in ManagedDirs)
            {
                var full = Path.Combine(RootDir, dir);
                if (!Directory.Exists(full)) continue;
                foreach (var file in Directory.GetFiles(full, "*" + AsideSuffix, SearchOption.AllDirectories))
                    try { File.Delete(file); } catch { /* still locked, try next time */ }
            }
        }
        catch (Exception e)
        {
            Log.Warning($"[Multiplayer] Failed cleaning up old update files: {e}");
        }
    }
}
